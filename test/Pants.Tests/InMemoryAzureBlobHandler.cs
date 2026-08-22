using System.Net;
using System.Net.Http.Headers;

namespace Pants.Tests;

sealed class InMemoryAzureBlobHandler : HttpMessageHandler
{
    readonly Lock _gate = new();
    readonly Dictionary<string, (byte[] Data, long Version)> _objects = new(StringComparer.Ordinal);

    public bool FailWalWrites { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var key = request.RequestUri!.AbsolutePath;
        if (FailWalWrites && request.Method == HttpMethod.Put &&
            key.Contains("/wal/epochs/", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        if (request.Method == HttpMethod.Get)
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

    static string FormatVersion(long version) => $"\"{version}\"";
}
