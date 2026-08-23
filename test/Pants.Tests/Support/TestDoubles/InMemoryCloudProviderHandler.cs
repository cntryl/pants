using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Cntryl.Pants.Tests;

sealed class InMemoryCloudProviderHandler : HttpMessageHandler
{
    const string AzureProtocol = "azure";
    const string GcsJsonProtocol = "gcs-json";
    const string GcsXmlProtocol = "gcs-xml";
    const string S3Protocol = "s3";

    readonly Lock _gate = new();
    readonly Dictionary<string, (byte[] Data, long Version)> _objects =
        new(StringComparer.Ordinal);
    readonly HashSet<string> _observedObjectWrites = new(StringComparer.Ordinal);

    public bool ContainsObject(string host, string pathFragment)
    {
        lock (_gate)
        {
            return _observedObjectWrites.Any(key =>
                key.StartsWith(host + "|", StringComparison.Ordinal) &&
                key.Contains(pathFragment, StringComparison.Ordinal));
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var protocol = IdentifyProtocol(request);
        if (IsListRequest(request, protocol))
        {
            return CreateListResponse(request, protocol);
        }

        var identity = ReadObjectIdentity(request, protocol);
        if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head)
        {
            return CreateReadResponse(request, protocol, identity);
        }

        if (request.Method == HttpMethod.Delete)
        {
            return Delete(request, protocol, identity);
        }

        if (request.Method != HttpMethod.Put && request.Method != HttpMethod.Post)
        {
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        var data = await request.Content!.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return Put(request, protocol, identity, data);
    }

    HttpResponseMessage CreateReadResponse(
        HttpRequestMessage request,
        string protocol,
        string identity)
    {
        lock (_gate)
        {
            if (!_objects.TryGetValue(identity, out var value))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (protocol == GcsJsonProtocol &&
                request.Method == HttpMethod.Get &&
                !StringComparer.Ordinal.Equals(GetQueryValue(request.RequestUri!, "alt"), "media"))
            {
                var metadata = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["size"] = value.Data.Length.ToString(CultureInfo.InvariantCulture),
                    ["etag"] = FormatEtag(value.Version),
                    ["generation"] = value.Version.ToString(CultureInfo.InvariantCulture)
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(metadata, Encoding.UTF8, "application/json")
                };
            }

            return CreateObjectResponse(HttpStatusCode.OK, protocol, value);
        }
    }

    HttpResponseMessage Put(
        HttpRequestMessage request,
        string protocol,
        string identity,
        byte[] data)
    {
        lock (_gate)
        {
            var exists = _objects.TryGetValue(identity, out var current);
            if (!ConditionMatches(request, protocol, exists, current.Version))
            {
                return CreatePredicateFailure(protocol);
            }

            var next = (data, exists ? checked(current.Version + 1) : 1);
            _objects[identity] = next;
            _observedObjectWrites.Add(identity);
            return CreateObjectResponse(HttpStatusCode.Created, protocol, next);
        }
    }

    HttpResponseMessage Delete(
        HttpRequestMessage request,
        string protocol,
        string identity)
    {
        lock (_gate)
        {
            if (!_objects.TryGetValue(identity, out var current))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (!ConditionMatches(request, protocol, exists: true, current.Version))
            {
                return CreatePredicateFailure(protocol);
            }

            _objects.Remove(identity);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    HttpResponseMessage CreateListResponse(HttpRequestMessage request, string protocol)
    {
        var uri = request.RequestUri!;
        var bucket = ReadBucket(uri, protocol);
        var prefix = GetQueryValue(uri, "prefix") ?? string.Empty;
        string[] objectKeys;
        lock (_gate)
        {
            var identityPrefix = $"{uri.Host}|{bucket}|";
            objectKeys = _objects.Keys
                .Where(key => key.StartsWith(identityPrefix, StringComparison.Ordinal))
                .Select(key => key[identityPrefix.Length..])
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();
        }

        var content = protocol switch
        {
            AzureProtocol => CreateAzureList(objectKeys),
            GcsJsonProtocol => JsonSerializer.Serialize(new
            {
                items = objectKeys.Select(static name => new { name }).ToArray()
            }),
            GcsXmlProtocol => CreateGcsXmlList(objectKeys),
            S3Protocol => CreateS3List(objectKeys),
            _ => throw new InvalidOperationException("Unknown provider protocol.")
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8)
        };
    }

    static bool ConditionMatches(
        HttpRequestMessage request,
        string protocol,
        bool exists,
        long currentVersion)
    {
        if (protocol is GcsJsonProtocol or GcsXmlProtocol)
        {
            var expected = protocol == GcsJsonProtocol
                ? GetQueryValue(request.RequestUri!, "ifGenerationMatch")
                : request.Headers.TryGetValues("x-goog-if-generation-match", out var generations)
                    ? generations.Single()
                    : null;
            if (expected == "0")
            {
                return !exists;
            }

            return expected is null ||
                exists && StringComparer.Ordinal.Equals(
                    expected,
                    currentVersion.ToString(CultureInfo.InvariantCulture));
        }

        if (request.Headers.IfNoneMatch.Any(static value => value.Tag == "*"))
        {
            return !exists;
        }

        var expectedEtag = request.Headers.IfMatch.SingleOrDefault()?.Tag;
        return expectedEtag is null ||
            exists && StringComparer.Ordinal.Equals(expectedEtag, FormatEtag(currentVersion));
    }

    static HttpResponseMessage CreateObjectResponse(
        HttpStatusCode statusCode,
        string protocol,
        (byte[] Data, long Version) value)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(value.Data)
        };
        response.Headers.ETag = new EntityTagHeaderValue(FormatEtag(value.Version));
        if (protocol is GcsJsonProtocol or GcsXmlProtocol)
        {
            response.Headers.TryAddWithoutValidation(
                "x-goog-generation",
                value.Version.ToString(CultureInfo.InvariantCulture));
        }

        return response;
    }

    static HttpResponseMessage CreatePredicateFailure(string protocol)
    {
        var content = protocol switch
        {
            AzureProtocol => "<Error><Code>ConditionNotMet</Code></Error>",
            GcsJsonProtocol =>
                "{\"error\":{\"errors\":[{\"reason\":\"conditionNotMet\"}]}}",
            GcsXmlProtocol or S3Protocol =>
                "<Error><Code>PreconditionFailed</Code></Error>",
            _ => throw new InvalidOperationException("Unknown provider protocol.")
        };
        var response = new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
        {
            Content = new StringContent(content, Encoding.UTF8)
        };
        if (protocol == AzureProtocol)
        {
            response.Headers.TryAddWithoutValidation("x-ms-error-code", "ConditionNotMet");
        }

        return response;
    }

    static string ReadObjectIdentity(HttpRequestMessage request, string protocol)
    {
        var uri = request.RequestUri!;
        var bucket = ReadBucket(uri, protocol);
        var objectKey = protocol == GcsJsonProtocol
            ? ReadGcsJsonObjectKey(uri)
            : protocol == S3Protocol && IsVirtualHostedS3(uri)
                ? Unescape(uri.AbsolutePath.Trim('/'))
                : Unescape(string.Join('/', uri.AbsolutePath.Trim('/').Split('/').Skip(1)));
        return $"{uri.Host}|{bucket}|{objectKey}";
    }

    static string ReadBucket(Uri uri, string protocol)
    {
        if (protocol == GcsJsonProtocol)
        {
            var marker = "/b/";
            var start = uri.AbsolutePath.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                throw new InvalidOperationException("GCS JSON request did not contain a bucket.");
            }

            start += marker.Length;
            var end = uri.AbsolutePath.IndexOf('/', start);
            return Unescape(uri.AbsolutePath[start..end]);
        }

        if (protocol == S3Protocol && IsVirtualHostedS3(uri))
        {
            return uri.Host.Split('.')[0];
        }

        return Unescape(uri.AbsolutePath.Trim('/').Split('/')[0]);
    }

    static string ReadGcsJsonObjectKey(Uri uri)
    {
        if (uri.AbsolutePath.StartsWith("/upload/", StringComparison.Ordinal))
        {
            return GetQueryValue(uri, "name") ??
                throw new InvalidOperationException("GCS JSON upload did not contain an object name.");
        }

        var marker = "/o/";
        var start = uri.AbsolutePath.IndexOf(marker, StringComparison.Ordinal);
        return start < 0
            ? throw new InvalidOperationException("GCS JSON request did not contain an object name.")
            : Unescape(uri.AbsolutePath[(start + marker.Length)..]);
    }

    static bool IsListRequest(HttpRequestMessage request, string protocol)
    {
        var uri = request.RequestUri!;
        return protocol switch
        {
            AzureProtocol => StringComparer.Ordinal.Equals(GetQueryValue(uri, "comp"), "list"),
            GcsJsonProtocol =>
                request.Method == HttpMethod.Get &&
                uri.AbsolutePath.EndsWith("/o", StringComparison.Ordinal),
            GcsXmlProtocol =>
                request.Method == HttpMethod.Get &&
                GetQueryValue(uri, "prefix") is not null,
            S3Protocol => StringComparer.Ordinal.Equals(GetQueryValue(uri, "list-type"), "2"),
            _ => false
        };
    }

    static string IdentifyProtocol(HttpRequestMessage request)
    {
        if (request.RequestUri!.AbsolutePath.Contains("/storage/v1/", StringComparison.Ordinal))
        {
            return GcsJsonProtocol;
        }

        if (request.Headers.Contains("x-ms-version"))
        {
            return AzureProtocol;
        }

        if (request.Headers.Contains("x-amz-content-sha256"))
        {
            return S3Protocol;
        }

        if (StringComparer.Ordinal.Equals(request.Headers.Authorization?.Scheme, "GOOG1"))
        {
            return GcsXmlProtocol;
        }

        throw new InvalidOperationException("The cloud provider protocol could not be identified.");
    }

    static string? GetQueryValue(Uri uri, string name)
    {
        foreach (var parameter in uri.Query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = parameter.Split('=', 2);
            if (StringComparer.Ordinal.Equals(Unescape(pair[0]), name))
            {
                return pair.ElementAtOrDefault(1) is { } value
                    ? Unescape(value)
                    : string.Empty;
            }
        }

        return null;
    }

    static string CreateAzureList(IEnumerable<string> objectKeys) =>
        $"<EnumerationResults><Blobs>{string.Concat(objectKeys.Select(static key => $"<Blob><Name>{WebUtility.HtmlEncode(key)}</Name></Blob>"))}</Blobs><NextMarker /></EnumerationResults>";

    static string CreateGcsXmlList(IEnumerable<string> objectKeys) =>
        $"<ListBucketResult>{string.Concat(objectKeys.Select(static key => $"<Contents><Key>{WebUtility.HtmlEncode(key)}</Key></Contents>"))}<IsTruncated>false</IsTruncated></ListBucketResult>";

    static string CreateS3List(IEnumerable<string> objectKeys) =>
        $"<ListBucketResult>{string.Concat(objectKeys.Select(static key => $"<Contents><Key>{WebUtility.HtmlEncode(key)}</Key></Contents>"))}<IsTruncated>false</IsTruncated></ListBucketResult>";

    static string FormatEtag(long version) => $"\"{version}\"";

    static bool IsVirtualHostedS3(Uri uri) =>
        uri.Host.Contains(".s3.", StringComparison.OrdinalIgnoreCase);

    static string Unescape(string value) => Uri.UnescapeDataString(value);
}
