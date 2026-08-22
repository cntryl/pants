using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Pants;

internal sealed class GcsObjectStore : ICloudObjectStore
{
    private const int MaximumAttempts = 3;
    private readonly PantsCloudProviderConfiguration.Gcs _configuration;
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string _prefix;
    private readonly TimeSpan _timeout;
    private readonly GcsCredential _credential;

    public GcsObjectStore(
        PantsCloudProviderConfiguration.Gcs configuration,
        string prefix,
        HttpClient httpClient,
        TimeSpan timeout)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (timeout < TimeSpan.FromMilliseconds(1))
        {
            throw PantsException.InvalidArgument("GCS operation timeout must be at least one millisecond.");
        }

        _endpoint = configuration.Endpoint ?? new Uri("https://storage.googleapis.com/", UriKind.Absolute);
        _prefix = NormalizePrefix(prefix);
        _timeout = timeout;
        _credential = GcsCredentialResolver.Resolve(configuration.Credential, httpClient, timeout);
        if (_credential.HmacAccessId is not null && configuration.ApiStyle != PantsGcsApiStyle.Xml)
        {
            throw PantsException.InvalidArgument("GCS HMAC credentials require the XML API style.");
        }
    }

    public async ValueTask<CloudObject?> GetAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Get,
            objectKey,
            ReadOnlyMemory<byte>.Empty,
            condition: null,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response);
        byte[] data = await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        string version = response.Headers.TryGetValues("x-goog-generation", out var generations)
            ? generations.First()
            : response.Headers.ETag?.Tag ??
              throw new PantsIOException("GCS GET response did not include a generation or ETag.");
        return new CloudObject(data, version);
    }

    public async ValueTask<bool> PutAsync(
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition condition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Put,
            objectKey,
            data,
            condition,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }

        EnsureSuccess(response);
        return true;
    }

    private async ValueTask<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition? condition,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        for (var attempt = 1; ; attempt++)
        {
            using HttpRequestMessage request = await CreateRequestAsync(
                method,
                objectKey,
                data,
                condition,
                linked.Token).ConfigureAwait(false);
            try
            {
                HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    linked.Token).ConfigureAwait(false);
                if (!IsRetryable(response.StatusCode) || attempt >= MaximumAttempts)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PantsTimeoutException("GCS operation exceeded its deadline.", exception);
            }
            catch (HttpRequestException exception) when (attempt >= MaximumAttempts)
            {
                throw new PantsIOException("GCS transport failed after bounded retries.", exception);
            }
            catch (HttpRequestException)
            {
            }

            await Task.Yield();
        }
    }

    private async ValueTask<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition? condition,
        CancellationToken cancellationToken)
    {
        string fullKey = CombineKey(objectKey);
        Uri uri = _configuration.ApiStyle == PantsGcsApiStyle.Json
            ? BuildJsonUri(method, fullKey, condition)
            : BuildXmlUri(fullKey);
        var requestMethod = method == HttpMethod.Put &&
            _configuration.ApiStyle == PantsGcsApiStyle.Json
                ? HttpMethod.Post
                : method;
        var request = new HttpRequestMessage(requestMethod, uri);
        if (method == HttpMethod.Put)
        {
            request.Content = new ByteArrayContent(data.ToArray());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        if (_configuration.ApiStyle == PantsGcsApiStyle.Xml)
        {
            ApplyXmlCondition(request, condition);
        }

        if (_credential.TokenProvider is { } tokenProvider)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                await tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false));
        }
        else
        {
            SignHmac(request, data.Span);
        }

        return request;
    }

    private Uri BuildJsonUri(
        HttpMethod method,
        string fullKey,
        CloudObjectWriteCondition? condition)
    {
        string bucket = Uri.EscapeDataString(_configuration.Bucket);
        string escapedName = Uri.EscapeDataString(fullKey);
        string relative = method == HttpMethod.Get
            ? $"storage/v1/b/{bucket}/o/{escapedName}?alt=media"
            : $"upload/storage/v1/b/{bucket}/o?uploadType=media&name={escapedName}";
        string? generation = condition switch
        {
            null or CloudObjectWriteCondition.Unconditional => null,
            CloudObjectWriteCondition.IfAbsent => "0",
            CloudObjectWriteCondition.IfVersion expected => expected.Version,
            _ => throw PantsException.InvalidArgument("The cloud object write condition is invalid.")
        };
        if (generation is not null)
        {
            relative += $"&ifGenerationMatch={Uri.EscapeDataString(generation)}";
        }

        return new Uri($"{_endpoint.AbsoluteUri.TrimEnd('/')}/{relative}", UriKind.Absolute);
    }

    private Uri BuildXmlUri(string fullKey)
    {
        string escaped = string.Join('/', fullKey.Split('/').Select(Uri.EscapeDataString));
        return new Uri(
            $"{_endpoint.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(_configuration.Bucket)}/{escaped}",
            UriKind.Absolute);
    }

    private static void ApplyXmlCondition(
        HttpRequestMessage request,
        CloudObjectWriteCondition? condition)
    {
        switch (condition)
        {
            case null:
            case CloudObjectWriteCondition.Unconditional:
                break;
            case CloudObjectWriteCondition.IfAbsent:
                request.Headers.TryAddWithoutValidation("x-goog-if-generation-match", "0");
                break;
            case CloudObjectWriteCondition.IfVersion expected:
                request.Headers.TryAddWithoutValidation(
                    "x-goog-if-generation-match",
                    expected.Version);
                break;
            default:
                throw PantsException.InvalidArgument("The cloud object write condition is invalid.");
        }
    }

    private void SignHmac(HttpRequestMessage request, ReadOnlySpan<byte> payload)
    {
        _ = payload;
        var accessId = _credential.HmacAccessId ??
            throw new PantsInternalException("GCS HMAC access ID is unavailable.");
        var secret = _credential.HmacSecret ??
            throw new PantsInternalException("GCS HMAC secret is unavailable.");
        var date = DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture);
        request.Headers.Date = DateTimeOffset.ParseExact(date, "R", CultureInfo.InvariantCulture);
        var authorization = CreateGoog1Authorization(request, accessId, secret, date);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
    }

    internal static string CreateGoog1Authorization(
        HttpRequestMessage request,
        string accessId,
        string secret,
        string date)
    {
        var canonicalHeaders = string.Concat(request.Headers
            .Where(static header =>
                header.Key.StartsWith("x-goog-", StringComparison.OrdinalIgnoreCase))
            .SelectMany(static header => header.Value.Select(value => new
            {
                Name = header.Key.ToLowerInvariant(),
                Value = string.Join(' ', value.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries))
            }))
            .GroupBy(static header => header.Name, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group =>
                $"{group.Key}:{string.Join(',', group.Select(value => value.Value))}\n"));
        var contentMd5 = request.Content?.Headers.ContentMD5 is { } contentMd5Bytes
            ? Convert.ToBase64String(contentMd5Bytes)
            : string.Empty;
        var contentType = request.Content?.Headers.ContentType?.ToString() ?? string.Empty;
        var stringToSign = string.Join(
            '\n',
            request.Method.Method,
            contentMd5,
            contentType,
            date,
            canonicalHeaders + request.RequestUri!.AbsolutePath);
        byte[] key;
        try
        {
            key = Convert.FromBase64String(secret);
        }
        catch (FormatException)
        {
            key = Encoding.UTF8.GetBytes(secret);
        }

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, key);
        hmac.AppendData(Encoding.UTF8.GetBytes(stringToSign));
        var signature = Convert.ToBase64String(hmac.GetHashAndReset());
        return $"GOOG1 {accessId}:{signature}";
    }

    private string CombineKey(string objectKey)
    {
        string normalized = NormalizeObjectKey(objectKey);
        return string.IsNullOrEmpty(_prefix) ? normalized : $"{_prefix}/{normalized}";
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            string requestId = response.Headers.TryGetValues("x-guploader-uploadid", out var values)
                ? values.FirstOrDefault() ?? "unavailable"
                : "unavailable";
            throw new PantsIOException(
                $"GCS request failed with HTTP {(int)response.StatusCode}; request ID {requestId}.");
        }
    }

    private static string NormalizePrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        string normalized = prefix.Trim('/');
        return string.IsNullOrEmpty(normalized) ? string.Empty : NormalizeObjectKey(normalized);
    }

    private static string NormalizeObjectKey(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || objectKey.StartsWith('/') ||
            objectKey.EndsWith('/') || objectKey.Contains('\\') ||
            objectKey.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new PantsInvalidArgumentException("Cloud object key is unsafe or empty.");
        }

        return objectKey;
    }
}
