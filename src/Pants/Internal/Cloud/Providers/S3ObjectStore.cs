using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Pants;

internal sealed class S3ObjectStore : ICloudObjectStore
{
    private const int MaximumAttempts = 3;
    private static readonly string EmptyPayloadHash = Hex(SHA256.HashData([]));
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string _bucket;
    private readonly string _region;
    private readonly string _prefix;
    private readonly bool _pathStyle;
    private readonly TimeSpan _timeout;
    private readonly S3Credentials _credentials;

    public S3ObjectStore(
        PantsCloudProviderConfiguration provider,
        string prefix,
        HttpClient httpClient,
        TimeSpan timeout)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (timeout < TimeSpan.FromMilliseconds(1))
        {
            throw PantsException.InvalidArgument("S3 operation timeout must be at least one millisecond.");
        }

        (_bucket, _region, _endpoint, _pathStyle, _credentials) = provider switch
        {
            PantsCloudProviderConfiguration.AwsS3 aws => (
                aws.Bucket,
                aws.Region,
                new Uri($"https://{aws.Bucket}.s3.{aws.Region}.amazonaws.com/"),
                false,
                S3CredentialResolver.Resolve(aws.Credentials)),
            PantsCloudProviderConfiguration.S3Compatible compatible => (
                compatible.Bucket,
                compatible.Region,
                compatible.Endpoint,
                compatible.PathStyle,
                S3CredentialResolver.Resolve(compatible.Credentials)),
            _ => throw PantsException.InvalidArgument("An S3 provider configuration is required.")
        };
        _prefix = NormalizePrefix(prefix);
        _timeout = timeout;
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
        string version = response.Headers.ETag?.Tag ??
            throw new PantsIOException("S3 GET response did not include an ETag.");
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
            using HttpRequestMessage request = CreateRequest(method, objectKey, data, condition);
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
                throw new PantsTimeoutException("S3 operation exceeded its deadline.", exception);
            }
            catch (HttpRequestException exception) when (attempt >= MaximumAttempts)
            {
                throw new PantsIOException("S3 transport failed after bounded retries.", exception);
            }
            catch (HttpRequestException)
            {
            }

            await Task.Yield();
        }
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition? condition)
    {
        Uri uri = BuildObjectUri(objectKey);
        var request = new HttpRequestMessage(method, uri);
        if (method == HttpMethod.Put)
        {
            request.Content = new ByteArrayContent(data.ToArray());
        }

        switch (condition)
        {
            case null:
            case CloudObjectWriteCondition.Unconditional:
                break;
            case CloudObjectWriteCondition.IfAbsent:
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
                break;
            case CloudObjectWriteCondition.IfVersion expected:
                request.Headers.IfMatch.Add(new EntityTagHeaderValue(expected.Version));
                break;
            default:
                throw PantsException.InvalidArgument("The cloud object write condition is invalid.");
        }

        Sign(request, data.Span);
        return request;
    }

    private void Sign(HttpRequestMessage request, ReadOnlySpan<byte> payload)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string timestamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string payloadHash = payload.Length == 0 ? EmptyPayloadHash : Hex(SHA256.HashData(payload));
        request.Headers.TryAddWithoutValidation("x-amz-date", timestamp);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        if (_credentials.SessionToken is { } sessionToken)
        {
            request.Headers.TryAddWithoutValidation("x-amz-security-token", sessionToken);
        }

        string host = request.RequestUri!.IsDefaultPort
            ? request.RequestUri.Host
            : request.RequestUri.Authority;
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = timestamp
        };
        if (_credentials.SessionToken is { } token)
        {
            headers["x-amz-security-token"] = token;
        }

        string canonicalHeaders = string.Concat(headers.Select(static pair => $"{pair.Key}:{pair.Value.Trim()}\n"));
        string signedHeaders = string.Join(';', headers.Keys);
        string canonicalRequest = string.Join(
            '\n',
            request.Method.Method,
            request.RequestUri.AbsolutePath,
            request.RequestUri.Query.TrimStart('?'),
            canonicalHeaders,
            signedHeaders,
            payloadHash);
        string scope = $"{date}/{_region}/s3/aws4_request";
        string stringToSign = string.Join(
            '\n',
            "AWS4-HMAC-SHA256",
            timestamp,
            scope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        byte[] dateKey = Hmac(Encoding.UTF8.GetBytes("AWS4" + _credentials.SecretKey), date);
        byte[] regionKey = Hmac(dateKey, _region);
        byte[] serviceKey = Hmac(regionKey, "s3");
        byte[] signingKey = Hmac(serviceKey, "aws4_request");
        string signature = Hex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "AWS4-HMAC-SHA256",
            $"Credential={_credentials.AccessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    private Uri BuildObjectUri(string objectKey)
    {
        string normalized = NormalizeObjectKey(objectKey);
        string combined = string.IsNullOrEmpty(_prefix) ? normalized : $"{_prefix}/{normalized}";
        string escaped = string.Join('/', combined.Split('/').Select(Uri.EscapeDataString));
        if (_pathStyle)
        {
            return new Uri(
                $"{_endpoint.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(_bucket)}/{escaped}",
                UriKind.Absolute);
        }

        if (_endpoint.Host.StartsWith(_bucket + ".", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(_endpoint, escaped);
        }

        var builder = new UriBuilder(_endpoint)
        {
            Host = $"{_bucket}.{_endpoint.Host}",
            Path = $"{_endpoint.AbsolutePath.TrimEnd('/')}/{escaped}"
        };
        return builder.Uri;
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            string requestId = response.Headers.TryGetValues("x-amz-request-id", out var values)
                ? values.FirstOrDefault() ?? "unavailable"
                : "unavailable";
            throw new PantsIOException(
                $"S3 request failed with HTTP {(int)response.StatusCode}; request ID {requestId}.");
        }
    }

    private static byte[] Hmac(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

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
