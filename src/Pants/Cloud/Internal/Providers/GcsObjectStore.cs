using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cntryl.Pants.Cloud.Internal.Providers;

sealed class GcsObjectStore : ICloudObjectStore
{
    const int MaximumAttempts = 3;
    readonly PantsCloudProviderConfiguration.Gcs _configuration;
    readonly GcsCredential _credential;
    readonly Uri _endpoint;
    readonly HttpClient _httpClient;
    readonly string _prefix;
    readonly TimeSpan _timeout;

    public GcsObjectStore(
        PantsCloudProviderConfiguration.Gcs configuration,
        string prefix,
        HttpClient httpClient,
        TimeSpan timeout,
        HttpClient? credentialHttpClient = null)
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
        _credential = GcsCredentialResolver.Resolve(
            configuration.Credential,
            credentialHttpClient ?? httpClient,
            timeout);
        if (_credential.HmacAccessId is not null && configuration.ApiStyle != PantsGcsApiStyle.Xml)
        {
            throw PantsException.InvalidArgument("GCS HMAC credentials require the XML API style.");
        }
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
                null,
                token),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response);
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        _ = response.Headers.ETag?.Tag ??
            throw new PantsIOException("GCS GET response did not include an ETag.");
        var version = ReadGenerationHeader(response, "GET");
        return new CloudObject(data, version);
    }

    public async ValueTask<CloudObjectMetadata?> HeadAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        using var response = await SendReadAsync(
            token => CreateHeadRequestAsync(objectKey, token),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response);
        return _configuration.ApiStyle == PantsGcsApiStyle.Json
            ? await ReadJsonMetadataAsync(response, cancellationToken).ConfigureAwait(false)
            : ReadXmlMetadata(response);
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
            token => CreateListRequestAsync(fullPrefix, continuationToken, token),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        return _configuration.ApiStyle == PantsGcsApiStyle.Json
            ? ReadJsonListPage(body)
            : ReadXmlListPage(body);
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
            await HasGcsPredicateFailureAsync(response, cancellationToken).ConfigureAwait(false))
        {
            return CloudObjectDeleteOutcome.ConditionNotMet;
        }

        EnsureSuccess(response);
        return CloudObjectDeleteOutcome.Deleted;
    }

    ValueTask<HttpResponseMessage> SendReadAsync(
        Func<CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        CancellationToken cancellationToken) =>
        SendAsync(requestFactory, true, cancellationToken);

    ValueTask<HttpResponseMessage> SendMutationAsync(
        Func<CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        CancellationToken cancellationToken) =>
        SendAsync(requestFactory, false, cancellationToken);

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
                        $"GCS mutation outcome is indeterminate after HTTP {(int)statusCode}.");
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
                        "GCS mutation outcome is indeterminate after its request deadline elapsed.",
                        exception);
                }

                throw new PantsTimeoutException("GCS operation exceeded its deadline.", exception);
            }
            catch (HttpRequestException exception) when (!retryTransientFailures)
            {
                throw new PantsIOException(
                    "GCS mutation outcome is indeterminate after a transport failure.",
                    exception);
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

    async ValueTask<HttpRequestMessage> CreateObjectRequestAsync(
        HttpMethod method,
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition? condition,
        CancellationToken cancellationToken)
    {
        var fullKey = CombineKey(objectKey);
        var uri = _configuration.ApiStyle == PantsGcsApiStyle.Json
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

        await AuthorizeAsync(request, data, cancellationToken).ConfigureAwait(false);
        return request;
    }

    async ValueTask<HttpRequestMessage> CreateHeadRequestAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        var fullKey = CombineKey(objectKey);
        var request = new HttpRequestMessage(
            _configuration.ApiStyle == PantsGcsApiStyle.Json ? HttpMethod.Get : HttpMethod.Head,
            _configuration.ApiStyle == PantsGcsApiStyle.Json
                ? BuildJsonMetadataUri(fullKey)
                : BuildXmlUri(fullKey));
        await AuthorizeAsync(
            request,
            ReadOnlyMemory<byte>.Empty,
            cancellationToken).ConfigureAwait(false);
        return request;
    }

    async ValueTask<HttpRequestMessage> CreateListRequestAsync(
        string fullPrefix,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        var uri = _configuration.ApiStyle == PantsGcsApiStyle.Json
            ? BuildJsonListUri(fullPrefix, continuationToken)
            : BuildXmlListUri(fullPrefix, continuationToken);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        await AuthorizeAsync(
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
        var fullKey = CombineKey(objectKey);
        var request = new HttpRequestMessage(
            HttpMethod.Delete,
            _configuration.ApiStyle == PantsGcsApiStyle.Json
                ? BuildJsonDeleteUri(fullKey, condition)
                : BuildXmlUri(fullKey));
        if (_configuration.ApiStyle == PantsGcsApiStyle.Xml &&
            condition is CloudObjectDeleteCondition.IfVersion expected)
        {
            request.Headers.TryAddWithoutValidation(
                "x-goog-if-generation-match",
                RequireVersion(expected.Version));
        }
        else if (condition is not CloudObjectDeleteCondition.Unconditional &&
                 condition is not CloudObjectDeleteCondition.IfVersion)
        {
            throw PantsException.InvalidArgument(
                "The cloud object delete condition is invalid.");
        }

        await AuthorizeAsync(
            request,
            ReadOnlyMemory<byte>.Empty,
            cancellationToken).ConfigureAwait(false);
        return request;
    }

    async ValueTask AuthorizeAsync(
        HttpRequestMessage request,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        EnsureXmlBodylessFraming(request);

        if (_credential.TokenProvider is { } tokenProvider)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                await tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false));
            return;
        }

        SignHmac(request, payload.Span);
    }

    void EnsureXmlBodylessFraming(HttpRequestMessage request)
    {
        if (_configuration.ApiStyle != PantsGcsApiStyle.Xml ||
            request.Content is not null)
        {
            return;
        }

        // This supplies Content-Length: 0 without payload bytes, including for HEAD.
        // It is a GCS XML wire-framing requirement, not part of signing and not a
        // general convention shared by the S3 and Azure providers. Sqrzl's
        // provider-shaped GCS endpoint rejects these requests with 411 otherwise.
        request.Content = new ByteArrayContent([]);
    }

    Uri BuildJsonUri(
        HttpMethod method,
        string fullKey,
        CloudObjectWriteCondition? condition)
    {
        var bucket = Uri.EscapeDataString(_configuration.Bucket);
        var escapedName = Uri.EscapeDataString(fullKey);
        var relative = method == HttpMethod.Get
            ? $"storage/v1/b/{bucket}/o/{escapedName}?alt=media"
            : $"upload/storage/v1/b/{bucket}/o?uploadType=media&name={escapedName}";
        var generation = condition switch
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

    Uri BuildJsonMetadataUri(string fullKey) => new(
        $"{_endpoint.AbsoluteUri.TrimEnd('/')}/storage/v1/b/" +
        $"{Uri.EscapeDataString(_configuration.Bucket)}/o/{Uri.EscapeDataString(fullKey)}",
        UriKind.Absolute);

    Uri BuildJsonDeleteUri(string fullKey, CloudObjectDeleteCondition condition)
    {
        var builder = new UriBuilder(BuildJsonMetadataUri(fullKey));
        switch (condition)
        {
            case CloudObjectDeleteCondition.Unconditional:
                break;
            case CloudObjectDeleteCondition.IfVersion expected:
                builder.Query = "ifGenerationMatch=" +
                                Uri.EscapeDataString(RequireVersion(expected.Version));
                break;
            default:
                throw PantsException.InvalidArgument(
                    "The cloud object delete condition is invalid.");
        }

        return builder.Uri;
    }

    Uri BuildJsonListUri(string fullPrefix, string? continuationToken)
    {
        var builder = new UriBuilder(
            $"{_endpoint.AbsoluteUri.TrimEnd('/')}/storage/v1/b/" +
            $"{Uri.EscapeDataString(_configuration.Bucket)}/o")
        {
            Query = CreateListQuery("prefix", fullPrefix, "pageToken", continuationToken)
        };
        return builder.Uri;
    }

    Uri BuildXmlUri(string fullKey)
    {
        var escaped = string.Join('/', fullKey.Split('/').Select(Uri.EscapeDataString));
        return new Uri(
            $"{_endpoint.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(_configuration.Bucket)}/{escaped}",
            UriKind.Absolute);
    }

    Uri BuildXmlListUri(string fullPrefix, string? continuationToken)
    {
        var builder = new UriBuilder(
            $"{_endpoint.AbsoluteUri.TrimEnd('/')}/" +
            Uri.EscapeDataString(_configuration.Bucket))
        {
            Query = CreateListQuery("prefix", fullPrefix, "marker", continuationToken)
        };
        return builder.Uri;
    }

    static string CreateListQuery(
        string prefixName,
        string prefix,
        string tokenName,
        string? continuationToken)
    {
        var query = $"{Uri.EscapeDataString(prefixName)}={Uri.EscapeDataString(prefix)}";
        return continuationToken is null
            ? query
            : $"{query}&{Uri.EscapeDataString(tokenName)}=" +
              Uri.EscapeDataString(continuationToken);
    }

    static void ApplyXmlCondition(
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

    static async ValueTask<CloudObjectMetadata> ReadJsonMetadataAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new PantsIOException("GCS JSON metadata response must be an object.");
            }

            var sizeText = ReadRequiredJsonString(document.RootElement, "size", "metadata");
            if (!ulong.TryParse(
                    sizeText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var size))
            {
                throw new PantsIOException(
                    $"GCS JSON metadata response has invalid size '{sizeText}'.");
            }

            var etag = ReadRequiredJsonString(document.RootElement, "etag", "metadata");
            var generation = ValidateGeneration(
                ReadRequiredJsonString(document.RootElement, "generation", "metadata"),
                "metadata");
            DateTimeOffset? lastModified = null;
            if (document.RootElement.TryGetProperty("updated", out var updated))
            {
                if (updated.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(
                        updated.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var parsed))
                {
                    throw new PantsIOException(
                        "GCS JSON metadata response has an invalid updated timestamp.");
                }

                lastModified = parsed;
            }

            return new CloudObjectMetadata(size, etag, generation, lastModified);
        }
        catch (JsonException exception)
        {
            throw new PantsIOException("GCS JSON metadata response is malformed.", exception);
        }
    }

    static CloudObjectMetadata ReadXmlMetadata(HttpResponseMessage response)
    {
        var size = response.Content.Headers.ContentLength is { } contentLength && contentLength >= 0
            ? checked((ulong)contentLength)
            : throw new PantsIOException(
                "GCS XML metadata response did not include a valid Content-Length.");
        var etag = response.Headers.ETag?.Tag ??
                   throw new PantsIOException("GCS XML metadata response did not include an ETag.");
        return new CloudObjectMetadata(
            size,
            etag,
            ReadGenerationHeader(response, "metadata"),
            response.Content.Headers.LastModified);
    }

    CloudObjectListPage ReadJsonListPage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new PantsIOException("GCS LIST JSON response must be an object.");
            }

            var objectKeys = Array.Empty<string>();
            if (document.RootElement.TryGetProperty("items", out var items))
            {
                if (items.ValueKind != JsonValueKind.Array)
                {
                    throw new PantsIOException("GCS LIST JSON items must be an array.");
                }

                objectKeys = items.EnumerateArray()
                    .Select(static item =>
                        item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("name", out var name) &&
                        name.ValueKind == JsonValueKind.String
                            ? name.GetString()
                            : throw new PantsIOException(
                                "GCS LIST JSON item must contain a string name."))
                    .Select(key => StripConfiguredPrefix(key!))
                    .ToArray();
            }

            string? nextPageToken = null;
            if (document.RootElement.TryGetProperty("nextPageToken", out var token))
            {
                if (token.ValueKind != JsonValueKind.String)
                {
                    throw new PantsIOException(
                        "GCS LIST JSON nextPageToken must be a string.");
                }

                nextPageToken = token.GetString();
            }

            return new CloudObjectListPage(
                objectKeys,
                string.IsNullOrEmpty(nextPageToken) ? null : nextPageToken);
        }
        catch (JsonException exception)
        {
            throw new PantsIOException("GCS LIST JSON response is malformed.", exception);
        }
    }

    CloudObjectListPage ReadXmlListPage(string body)
    {
        var document = CloudProviderXml.ParseList(body, "ListBucketResult", "GCS XML");
        var remoteKeys = document.Descendants()
            .Where(static element => element.Name.LocalName == "Key")
            .Select(static element => element.Value)
            .ToArray();
        var truncated = document.Descendants()
            .FirstOrDefault(static element => element.Name.LocalName == "IsTruncated")?.Value
            .Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        var nextMarker = document.Descendants()
            .FirstOrDefault(static element => element.Name.LocalName == "NextMarker")?
            .Value;
        nextMarker = string.IsNullOrEmpty(nextMarker) ? null : nextMarker;
        if (truncated && nextMarker is null)
        {
            nextMarker = remoteKeys.LastOrDefault();
        }

        if (truncated && string.IsNullOrEmpty(nextMarker))
        {
            throw new PantsIOException(
                "GCS XML LIST response was truncated without NextMarker.");
        }

        return new CloudObjectListPage(
            remoteKeys.Select(StripConfiguredPrefix).ToArray(),
            truncated ? nextMarker : null);
    }

    async ValueTask<bool> HasGcsPredicateFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        string? reason;
        if (_configuration.ApiStyle == PantsGcsApiStyle.Xml)
        {
            reason = CloudProviderXml.TryReadElementValue(body, "Code");
        }
        else
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                reason = document.RootElement.TryGetProperty("error", out var error) &&
                         error.TryGetProperty("errors", out var errors) &&
                         errors.ValueKind == JsonValueKind.Array &&
                         errors.GetArrayLength() > 0 &&
                         errors[0].TryGetProperty("reason", out var value) &&
                         value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            }
            catch (JsonException)
            {
                reason = null;
            }
        }

        return reason is not null &&
               (reason.Equals("conditionNotMet", StringComparison.OrdinalIgnoreCase) ||
                reason.Equals("PreconditionFailed", StringComparison.OrdinalIgnoreCase));
    }

    static string ReadRequiredJsonString(
        JsonElement value,
        string propertyName,
        string operation) =>
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new PantsIOException(
                $"GCS JSON {operation} response has an invalid {propertyName}.");

    static string ReadGenerationHeader(HttpResponseMessage response, string operation)
    {
        var generation = response.Headers.TryGetValues(
            "x-goog-generation",
            out var generations)
            ? generations.FirstOrDefault()
            : null;
        return ValidateGeneration(generation, operation);
    }

    static string ValidateGeneration(string? generation, string operation)
    {
        if (string.IsNullOrWhiteSpace(generation) ||
            !ulong.TryParse(
                generation,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _))
        {
            throw new PantsIOException(
                $"GCS {operation} response did not include a valid generation.");
        }

        return generation;
    }

    static string RequireVersion(string version) =>
        string.IsNullOrWhiteSpace(version)
            ? throw new PantsInvalidArgumentException(
                "A non-empty cloud object version is required for conditional deletion.")
            : version;

    void SignHmac(HttpRequestMessage request, ReadOnlySpan<byte> payload)
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

    string CombineKey(string objectKey)
    {
        var normalized = NormalizeObjectKey(objectKey);
        return string.IsNullOrEmpty(_prefix) ? normalized : $"{_prefix}/{normalized}";
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
                $"GCS LIST returned object '{objectKey}' outside the configured prefix.");
    }

    static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var requestId = response.Headers.TryGetValues("x-guploader-uploadid", out var values)
                ? values.FirstOrDefault() ?? "unavailable"
                : "unavailable";
            throw new PantsIOException(
                $"GCS request failed with HTTP {(int)response.StatusCode}; request ID {requestId}.");
        }
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
