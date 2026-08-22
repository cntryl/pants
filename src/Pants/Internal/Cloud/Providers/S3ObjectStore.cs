using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Pants;

internal sealed class S3ObjectStore : ICloudObjectStore
{
    const int MaximumAttempts = 3;
    static readonly string EmptyPayloadHash = Hex(SHA256.HashData([]));
    readonly HttpClient _httpClient;
    readonly Uri _endpoint;
    readonly string _bucket;
    readonly string _region;
    readonly string _prefix;
    readonly bool _pathStyle;
    readonly TimeSpan _timeout;
    readonly IS3CredentialProvider _credentialProvider;

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

        (_bucket, _region, _endpoint, _pathStyle, _credentialProvider) = provider switch
        {
            PantsCloudProviderConfiguration.AwsS3 aws => (
                aws.Bucket,
                aws.Region,
                CreateAwsEndpoint(aws.Bucket, aws.Region),
                aws.Bucket.Contains('.'),
                S3CredentialResolver.Resolve(
                    aws.Credentials,
                    aws.Region,
                    allowAwsDefaultChain: true,
                    httpClient,
                    timeout)),
            PantsCloudProviderConfiguration.S3Compatible compatible => (
                compatible.Bucket,
                compatible.Region,
                compatible.Endpoint,
                compatible.PathStyle,
                S3CredentialResolver.Resolve(
                    compatible.Credentials,
                    compatible.Region,
                    allowAwsDefaultChain: false,
                    httpClient,
                    timeout)),
            _ => throw PantsException.InvalidArgument("An S3 provider configuration is required.")
        };
        _prefix = NormalizePrefix(prefix);
        _timeout = timeout;
    }

    public async ValueTask<CloudObject?> GetAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        using var response = await SendReadAsync(
            token => CreateObjectRequestAsync(
                HttpMethod.Get,
                objectKey,
                ReadOnlyMemory<byte>.Empty,
                condition: null,
                token),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response);
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var version = response.Headers.ETag?.Tag ??
            throw new PantsIOException("S3 GET response did not include an ETag.");
        return new CloudObject(data, version);
    }

    public async ValueTask<CloudObjectMetadata?> HeadAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        using var response = await SendReadAsync(
            token => CreateObjectRequestAsync(
                HttpMethod.Head,
                objectKey,
                ReadOnlyMemory<byte>.Empty,
                condition: null,
                token),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response);
        var size = response.Content.Headers.ContentLength is { } contentLength && contentLength >= 0
            ? checked((ulong)contentLength)
            : throw new PantsIOException("S3 HEAD response did not include a valid Content-Length.");
        var etag = response.Headers.ETag?.Tag ??
            throw new PantsIOException("S3 HEAD response did not include an ETag.");
        return new CloudObjectMetadata(
            size,
            etag,
            Generation: null,
            response.Content.Headers.LastModified);
    }

    public async ValueTask<bool> PutAsync(
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition condition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);
        using var response = await SendMutationAsync(
            token => CreateObjectRequestAsync(
                HttpMethod.Put,
                objectKey,
                data,
                condition,
                token),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }

        EnsureSuccess(response);
        return true;
    }

    public async ValueTask<CloudObjectListPage> ListPageAsync(
        string prefix,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        var fullPrefix = CombinePrefix(prefix);
        using var response = await SendReadAsync(
            token => CreateBucketRequestAsync(
                HttpMethod.Get,
                BuildListUri(fullPrefix, continuationToken),
                token),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        var document = CloudProviderXml.ParseList(body, "ListBucketResult", "S3");
        var objectKeys = document.Descendants()
            .Where(static element => element.Name.LocalName == "Contents")
            .Select(static content => content.Elements()
                .FirstOrDefault(static element => element.Name.LocalName == "Key")?.Value)
            .Where(static key => key is not null)
            .Select(key => StripConfiguredPrefix(key!))
            .ToArray();
        var truncated = document.Descendants()
            .FirstOrDefault(static element => element.Name.LocalName == "IsTruncated")?.Value
            .Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        var nextToken = document.Descendants()
            .FirstOrDefault(static element => element.Name.LocalName == "NextContinuationToken")?
            .Value;
        nextToken = string.IsNullOrEmpty(nextToken) ? null : nextToken;
        if (truncated && nextToken is null)
        {
            throw new PantsIOException(
                "S3 LIST response was truncated without NextContinuationToken.");
        }

        return new CloudObjectListPage(objectKeys, truncated ? nextToken : null);
    }

    public async ValueTask<CloudObjectDeleteOutcome> DeleteAsync(
        string objectKey,
        CloudObjectDeleteCondition condition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);
        using var response = await SendMutationAsync(
            token => CreateDeleteRequestAsync(objectKey, condition, token),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return CloudObjectDeleteOutcome.NotFound;
        }

        if (response.StatusCode == HttpStatusCode.PreconditionFailed &&
            condition is CloudObjectDeleteCondition.IfVersion &&
            await HasS3ErrorCodeAsync(
                response,
                "PreconditionFailed",
                cancellationToken).ConfigureAwait(false))
        {
            return CloudObjectDeleteOutcome.ConditionNotMet;
        }

        EnsureSuccess(response);
        return CloudObjectDeleteOutcome.Deleted;
    }

    ValueTask<HttpResponseMessage> SendReadAsync(
        Func<CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        CancellationToken cancellationToken) =>
        SendAsync(requestFactory, retryTransientFailures: true, cancellationToken);

    ValueTask<HttpResponseMessage> SendMutationAsync(
        Func<CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        CancellationToken cancellationToken) =>
        SendAsync(requestFactory, retryTransientFailures: false, cancellationToken);

    async ValueTask<HttpResponseMessage> SendAsync(
        Func<CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        bool retryTransientFailures,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = await requestFactory(linked.Token).ConfigureAwait(false);
                var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    linked.Token).ConfigureAwait(false);
                if (!retryTransientFailures && IsRetryable(response.StatusCode))
                {
                    var statusCode = response.StatusCode;
                    response.Dispose();
                    throw new PantsIOException(
                        $"S3 mutation outcome is indeterminate after HTTP {(int)statusCode}.");
                }

                if (!retryTransientFailures ||
                    !IsRetryable(response.StatusCode) ||
                    attempt >= MaximumAttempts)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (!retryTransientFailures)
                {
                    throw new PantsIOException(
                        "S3 mutation outcome is indeterminate after its request deadline elapsed.",
                        exception);
                }

                throw new PantsTimeoutException("S3 operation exceeded its deadline.", exception);
            }
            catch (HttpRequestException exception) when (!retryTransientFailures)
            {
                throw new PantsIOException(
                    "S3 mutation outcome is indeterminate after a transport failure.",
                    exception);
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

    async ValueTask<HttpRequestMessage> CreateObjectRequestAsync(
        HttpMethod method,
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition? condition,
        CancellationToken cancellationToken)
    {
        var uri = BuildObjectUri(objectKey);
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

        await SignAsync(request, data, cancellationToken).ConfigureAwait(false);
        return request;
    }

    async ValueTask<HttpRequestMessage> CreateBucketRequestAsync(
        HttpMethod method,
        Uri uri,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, uri);
        await SignAsync(
            request,
            ReadOnlyMemory<byte>.Empty,
            cancellationToken).ConfigureAwait(false);
        return request;
    }

    async ValueTask<HttpRequestMessage> CreateDeleteRequestAsync(
        string objectKey,
        CloudObjectDeleteCondition condition,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, BuildObjectUri(objectKey));
        switch (condition)
        {
            case CloudObjectDeleteCondition.Unconditional:
                break;
            case CloudObjectDeleteCondition.IfVersion expected:
                request.Headers.IfMatch.Add(new EntityTagHeaderValue(
                    RequireVersion(expected.Version)));
                break;
            default:
                throw PantsException.InvalidArgument(
                    "The cloud object delete condition is invalid.");
        }

        await SignAsync(
            request,
            ReadOnlyMemory<byte>.Empty,
            cancellationToken).ConfigureAwait(false);
        return request;
    }

    async ValueTask SignAsync(
        HttpRequestMessage request,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var credentials = await _credentialProvider.GetCredentialsAsync(cancellationToken)
            .ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var payloadHash = payload.Length == 0
            ? EmptyPayloadHash
            : Hex(SHA256.HashData(payload.Span));
        request.Headers.TryAddWithoutValidation("x-amz-date", timestamp);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        if (credentials.SessionToken is { } sessionToken)
        {
            request.Headers.TryAddWithoutValidation("x-amz-security-token", sessionToken);
        }

        var host = request.RequestUri!.IsDefaultPort
            ? request.RequestUri.Host
            : request.RequestUri.Authority;
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = timestamp
        };
        if (credentials.SessionToken is { } token)
        {
            headers["x-amz-security-token"] = token;
        }

        var canonicalHeaders = string.Concat(headers.Select(static pair => $"{pair.Key}:{pair.Value.Trim()}\n"));
        var signedHeaders = string.Join(';', headers.Keys);
        var canonicalRequest = string.Join(
            '\n',
            request.Method.Method,
            request.RequestUri.AbsolutePath,
            CanonicalizeQuery(request.RequestUri),
            canonicalHeaders,
            signedHeaders,
            payloadHash);
        var scope = $"{date}/{_region}/s3/aws4_request";
        var stringToSign = string.Join(
            '\n',
            "AWS4-HMAC-SHA256",
            timestamp,
            scope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        var dateKey = Hmac(Encoding.UTF8.GetBytes("AWS4" + credentials.SecretKey), date);
        var regionKey = Hmac(dateKey, _region);
        var serviceKey = Hmac(regionKey, "s3");
        var signingKey = Hmac(serviceKey, "aws4_request");
        var signature = Hex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "AWS4-HMAC-SHA256",
            $"Credential={credentials.AccessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    Uri BuildObjectUri(string objectKey)
    {
        var normalized = NormalizeObjectKey(objectKey);
        var combined = string.IsNullOrEmpty(_prefix) ? normalized : $"{_prefix}/{normalized}";
        var escaped = string.Join('/', combined.Split('/').Select(Uri.EscapeDataString));
        return new Uri(BuildBucketUri(), escaped);
    }

    Uri BuildBucketUri()
    {
        if (_pathStyle)
        {
            return new Uri(
                $"{_endpoint.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(_bucket)}/",
                UriKind.Absolute);
        }

        if (_endpoint.Host.StartsWith(_bucket + ".", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(_endpoint.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        }

        var builder = new UriBuilder(_endpoint)
        {
            Host = $"{_bucket}.{_endpoint.Host}",
            Path = _endpoint.AbsolutePath.TrimEnd('/') + "/"
        };
        return builder.Uri;
    }

    Uri BuildListUri(string fullPrefix, string? continuationToken)
    {
        var builder = new UriBuilder(BuildBucketUri());
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("list-type", "2"),
            new("prefix", fullPrefix)
        };
        if (continuationToken is not null)
        {
            parameters.Add(new("continuation-token", continuationToken));
        }

        builder.Query = string.Join(
            '&',
            parameters.Select(static parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
        return builder.Uri;
    }

    string CombinePrefix(string prefix)
    {
        var normalized = NormalizeListPrefix(prefix);
        return string.IsNullOrEmpty(_prefix)
            ? normalized
            : string.IsNullOrEmpty(normalized)
                ? _prefix + "/"
                : $"{_prefix}/{normalized}";
    }

    string StripConfiguredPrefix(string objectKey)
    {
        if (string.IsNullOrEmpty(_prefix))
        {
            return NormalizeObjectKey(objectKey);
        }

        var namespacePrefix = _prefix + "/";
        return objectKey.StartsWith(namespacePrefix, StringComparison.Ordinal)
            ? NormalizeObjectKey(objectKey[namespacePrefix.Length..])
            : throw new PantsIOException(
                $"S3 LIST returned object '{objectKey}' outside the configured prefix.");
    }

    static async ValueTask<bool> HasS3ErrorCodeAsync(
        HttpResponseMessage response,
        string expectedCode,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        return StringComparer.OrdinalIgnoreCase.Equals(
            CloudProviderXml.TryReadElementValue(body, "Code"),
            expectedCode);
    }

    static string CanonicalizeQuery(Uri uri) => string.Join(
        '&',
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static parameter => parameter.Split('=', 2))
            .Select(static pair => new KeyValuePair<string, string>(
                Uri.EscapeDataString(Uri.UnescapeDataString(pair[0])),
                Uri.EscapeDataString(Uri.UnescapeDataString(pair.ElementAtOrDefault(1) ?? string.Empty))))
            .OrderBy(static parameter => parameter.Key, StringComparer.Ordinal)
            .ThenBy(static parameter => parameter.Value, StringComparer.Ordinal)
            .Select(static parameter => $"{parameter.Key}={parameter.Value}"));

    static string RequireVersion(string version) =>
        string.IsNullOrWhiteSpace(version)
            ? throw new PantsInvalidArgumentException(
                "A non-empty cloud object version is required for conditional deletion.")
            : version;

    static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var requestId = response.Headers.TryGetValues("x-amz-request-id", out var values)
                ? values.FirstOrDefault() ?? "unavailable"
                : "unavailable";
            throw new PantsIOException(
                $"S3 request failed with HTTP {(int)response.StatusCode}; request ID {requestId}.");
        }
    }

    static byte[] Hmac(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    static Uri CreateAwsEndpoint(string bucket, string region)
    {
        var dnsSuffix = region.StartsWith("cn-", StringComparison.Ordinal)
            ? "amazonaws.com.cn"
            : "amazonaws.com";
        var host = bucket.Contains('.')
            ? $"s3.{region}.{dnsSuffix}"
            : $"{bucket}.s3.{region}.{dnsSuffix}";
        return new Uri($"https://{host}/");
    }

    static string NormalizePrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var normalized = prefix.Trim('/');
        return string.IsNullOrEmpty(normalized) ? string.Empty : NormalizeObjectKey(normalized);
    }

    static string NormalizeListPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (prefix.StartsWith('/') || prefix.Contains('\\') ||
            prefix.Split('/').Any(static segment => segment is "." or ".."))
        {
            throw new PantsInvalidArgumentException("Cloud object list prefix is unsafe.");
        }

        return prefix;
    }

    static string NormalizeObjectKey(string objectKey)
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
