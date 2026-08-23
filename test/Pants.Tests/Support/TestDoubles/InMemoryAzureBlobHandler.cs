using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace Cntryl.Pants.Tests.Support.TestDoubles;

sealed class InMemoryAzureBlobHandler : HttpMessageHandler
{
    readonly TaskCompletionSource _failedWalWrite =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    readonly Lock _gate = new();
    readonly Dictionary<string, (byte[] Data, long Version)> _objects = new(StringComparer.Ordinal);
    bool _acknowledgeMetadataWritesWithoutPersisting;
    bool _acknowledgeSstWritesWithoutPersisting;
    bool _acknowledgeWalCatalogWritesWithoutPersisting;
    bool _acknowledgeWalWritesWithoutPersisting;
    bool _failMetadataWrites;
    bool _failSstDeletes;
    bool _failSstList;
    bool _failWalWrites;
    int _failedWalWriteAttempts;
    int _sstDeleteAttempts;
    int _unconditionalSstDeleteAttempts;

    public bool FailWalWrites
    {
        get => Volatile.Read(ref _failWalWrites);
        set => Volatile.Write(ref _failWalWrites, value);
    }

    public bool FailMetadataWrites
    {
        get => Volatile.Read(ref _failMetadataWrites);
        set => Volatile.Write(ref _failMetadataWrites, value);
    }

    public bool AcknowledgeWalWritesWithoutPersisting
    {
        get => Volatile.Read(ref _acknowledgeWalWritesWithoutPersisting);
        set => Volatile.Write(ref _acknowledgeWalWritesWithoutPersisting, value);
    }

    public bool AcknowledgeWalCatalogWritesWithoutPersisting
    {
        get => Volatile.Read(ref _acknowledgeWalCatalogWritesWithoutPersisting);
        set => Volatile.Write(ref _acknowledgeWalCatalogWritesWithoutPersisting, value);
    }

    public bool AcknowledgeSstWritesWithoutPersisting
    {
        get => Volatile.Read(ref _acknowledgeSstWritesWithoutPersisting);
        set => Volatile.Write(ref _acknowledgeSstWritesWithoutPersisting, value);
    }

    public bool AcknowledgeMetadataWritesWithoutPersisting
    {
        get => Volatile.Read(ref _acknowledgeMetadataWritesWithoutPersisting);
        set => Volatile.Write(ref _acknowledgeMetadataWritesWithoutPersisting, value);
    }

    public bool FailSstList
    {
        get => Volatile.Read(ref _failSstList);
        set => Volatile.Write(ref _failSstList, value);
    }

    public bool FailSstDeletes
    {
        get => Volatile.Read(ref _failSstDeletes);
        set => Volatile.Write(ref _failSstDeletes, value);
    }

    public int FailedWalWriteAttempts => Volatile.Read(ref _failedWalWriteAttempts);

    public int SstDeleteAttempts => Volatile.Read(ref _sstDeleteAttempts);

    public int UnconditionalSstDeleteAttempts =>
        Volatile.Read(ref _unconditionalSstDeleteAttempts);

    public Task WaitForFailedWalWriteAsync(CancellationToken cancellationToken) =>
        _failedWalWrite.Task.WaitAsync(cancellationToken);

    public bool ContainsObjectPath(string pathFragment)
    {
        lock (_gate)
        {
            return _objects.Keys.Any(path => path.Contains(pathFragment, StringComparison.Ordinal));
        }
    }

    public string[] GetObjectPaths(string pathFragment)
    {
        lock (_gate)
        {
            return _objects.Keys
                .Where(path => path.Contains(pathFragment, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
    }

    public string GetObjectText(string pathFragment)
    {
        lock (_gate)
        {
            var value = _objects.Single(pair =>
                pair.Key.Contains(pathFragment, StringComparison.Ordinal)).Value;
            return Encoding.UTF8.GetString(value.Data);
        }
    }

    public void ReplaceObjectText(string pathFragment, string value)
    {
        lock (_gate)
        {
            var key = _objects.Keys.Single(path =>
                path.Contains(pathFragment, StringComparison.Ordinal));
            var current = _objects[key];
            _objects[key] = (
                Encoding.UTF8.GetBytes(value),
                current.Version + 1);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var key = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get &&
            StringComparer.Ordinal.Equals(
                GetQueryParameter(request.RequestUri, "comp"),
                "list"))
        {
            if (FailSstList &&
                GetQueryParameter(request.RequestUri, "prefix")?.Contains(
                    "/sst/",
                    StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return CreateListResponse(request.RequestUri);
        }

        if (FailWalWrites && request.Method == HttpMethod.Put &&
            key.Contains("/wal/epochs/", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _failedWalWriteAttempts);
            _failedWalWrite.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        if (FailMetadataWrites && request.Method == HttpMethod.Put &&
            key.Contains("/metadata/", StringComparison.Ordinal) &&
            !key.EndsWith("/metadata/ddl.registry.json", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        if (request.Method == HttpMethod.Put &&
            ((AcknowledgeWalWritesWithoutPersisting &&
              key.Contains("/wal/epochs/", StringComparison.Ordinal)) ||
             (AcknowledgeWalCatalogWritesWithoutPersisting &&
              key.EndsWith("/wal/publication-catalog.v1.json", StringComparison.Ordinal)) ||
             (AcknowledgeSstWritesWithoutPersisting &&
              key.Contains("/sst/", StringComparison.Ordinal) &&
              key.EndsWith(".sst", StringComparison.Ordinal)) ||
             (AcknowledgeMetadataWritesWithoutPersisting &&
              key.Contains("/metadata/", StringComparison.Ordinal) &&
              !key.EndsWith("/metadata/ddl.registry.json", StringComparison.Ordinal))))
        {
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Headers = { ETag = new EntityTagHeaderValue("\"ignored\"") }
            };
        }

        if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head)
        {
            lock (_gate)
            {
                if (!_objects.TryGetValue(key, out var value))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                return CreateResponse(HttpStatusCode.OK, value);
            }
        }

        if (request.Method == HttpMethod.Delete)
        {
            if (key.Contains("/sst/", StringComparison.Ordinal) &&
                key.EndsWith(".sst", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _sstDeleteAttempts);
                if (request.Headers.IfMatch.Count == 0)
                {
                    Interlocked.Increment(ref _unconditionalSstDeleteAttempts);
                }

                if (FailSstDeletes)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }
            }

            lock (_gate)
            {
                if (!_objects.TryGetValue(key, out var current))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                var expected = request.Headers.IfMatch.SingleOrDefault()?.Tag;
                if (expected is not null &&
                    !StringComparer.Ordinal.Equals(expected, FormatVersion(current.Version)))
                {
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                }

                _objects.Remove(key);
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
        }

        var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        lock (_gate)
        {
            var exists = _objects.TryGetValue(key, out var current);
            if (request.Headers.IfNoneMatch.Any(static value => value.Tag == "*") && exists)
            {
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            }

            var expected = request.Headers.IfMatch.SingleOrDefault()?.Tag;
            if (expected is not null &&
                (!exists || !StringComparer.Ordinal.Equals(expected, FormatVersion(current.Version))))
            {
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            }

            var next = (bytes, exists ? current.Version + 1 : 1);
            _objects[key] = next;
            return CreateResponse(HttpStatusCode.Created, next);
        }
    }

    static HttpResponseMessage CreateResponse(
        HttpStatusCode status,
        (byte[] Data, long Version) value) =>
        new(status)
        {
            Content = new ByteArrayContent(value.Data),
            Headers = { ETag = new EntityTagHeaderValue(FormatVersion(value.Version)) }
        };

    HttpResponseMessage CreateListResponse(Uri requestUri)
    {
        var prefix = GetQueryParameter(requestUri, "prefix") ?? string.Empty;
        string[] names;
        lock (_gate)
        {
            names = _objects.Keys
                .Where(static path => path.StartsWith("/container/", StringComparison.Ordinal))
                .Select(static path => Uri.UnescapeDataString(path["/container/".Length..]))
                .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();
        }

        var document = new XDocument(
            new XElement(
                "EnumerationResults",
                new XElement(
                    "Blobs",
                    names.Select(name => new XElement(
                        "Blob",
                        new XElement("Name", name)))),
                new XElement("NextMarker")));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                document.ToString(SaveOptions.DisableFormatting),
                Encoding.UTF8,
                "application/xml")
        };
    }

    static string? GetQueryParameter(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (StringComparer.Ordinal.Equals(Uri.UnescapeDataString(parts[0]), name))
            {
                return Uri.UnescapeDataString(parts.ElementAtOrDefault(1) ?? string.Empty);
            }
        }

        return null;
    }

    static string FormatVersion(long version) => $"\"{version}\"";
}
