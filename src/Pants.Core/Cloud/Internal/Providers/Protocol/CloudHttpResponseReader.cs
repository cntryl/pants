using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace Cntryl.Pants.Cloud.Internal.Providers.Protocol;

static class CloudHttpResponseReader
{
    public static async ValueTask<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get)
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (request.Headers.Range is { } range)
            {
                if (!response.IsSuccessStatusCode)
                {
                    // Ranged reads classify errors by status (and response headers), never by
                    // their body. Do not download an unbounded error page before retrying.
                    response.Content.Dispose();
                    response.Content = new ByteArrayContent([]);
                    return response;
                }

                await BufferRangeAsync(response, range, client.MaxResponseContentBufferSize, cancellationToken)
                    .ConfigureAwait(false);
                return response;
            }

            // Buffering can replace malformed headers with a computed length. Capture and validate
            // the declaration first, then buffer under the caller's existing operation deadline.
            var declaredLength = response.IsSuccessStatusCode ? ReadDeclaredLength(response) : null;
            await response.Content.LoadIntoBufferAsync(client.MaxResponseContentBufferSize, cancellationToken)
                .ConfigureAwait(false);
            if (declaredLength is { } expected)
            {
                var buffered = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                if ((ulong)buffered.Length != expected)
                {
                    throw InvalidLength();
                }
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    static async ValueTask BufferRangeAsync(
        HttpResponseMessage response,
        RangeHeaderValue requested,
        long maximumBufferSize,
        CancellationToken cancellationToken)
    {
        var range = requested.Ranges.Count == 1 ? requested.Ranges.Single() : null;
        if (response.StatusCode != HttpStatusCode.PartialContent ||
            !requested.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) ||
            range?.From is not { } start || range.To is not { } end ||
            !response.Content.Headers.NonValidated.TryGetValues("Content-Range", out var values) ||
            values.Count != 1 ||
            !ContentRangeHeaderValue.TryParse(values.Single(), out var actual) ||
            !actual.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) ||
            actual.From != start || actual.To != end)
        {
            throw new PantsIOException("Cloud ranged GET did not return the requested Content-Range.");
        }

        var length = checked(end - start + 1);
        if (length > maximumBufferSize || length > int.MaxValue)
        {
            throw new PantsIOException("Cloud ranged GET exceeds the configured response buffer limit.");
        }

        if (ReadDeclaredLength(response) is { } declared && declared != (ulong)length)
        {
            throw InvalidLength();
        }

        var original = response.Content;
        var stream = await original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var data = new byte[(int)length];
        try
        {
            await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new PantsIOException("Cloud ranged GET returned a truncated body.", exception);
        }

        // Confirm EOF without draining or retaining an oversized response. This read remains
        // inside the same operation deadline as the headers and requested bytes.
        if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
        {
            throw InvalidLength();
        }

        var buffered = new ByteArrayContent(data);
        foreach (var header in original.Headers.NonValidated)
        {
            buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content = buffered;
        original.Dispose();
    }

    static ulong? ReadDeclaredLength(HttpResponseMessage response)
    {
        // Read raw values: a parsed ContentLength can hide malformed or conflicting headers.
        // An absent declaration is valid for responses such as chunked transfers.
        if (!response.Content.Headers.NonValidated.TryGetValues("Content-Length", out var values))
        {
            return null;
        }

        ulong? expected = null;
        foreach (var value in values)
        {
            if (!ulong.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var declaredLength) ||
                expected is { } previous && declaredLength != previous)
            {
                throw InvalidLength();
            }

            expected = declaredLength;
        }

        return expected;
    }

    static PantsIOException InvalidLength() =>
        new("Cloud GET response has an invalid or inconsistent Content-Length.");
}
