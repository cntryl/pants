using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Cntryl.Pants;

internal sealed class AzureBlobObjectStore : ICloudObjectStore
{
    const int MaximumAttempts = 3;
    const string ServiceVersion = "2024-11-04";
    readonly HttpClient _httpClient;
    readonly Uri _containerEndpoint;
    readonly string _account;
    readonly string _prefix;
    readonly TimeSpan _timeout;
    readonly AzureResolvedCredential _credential;

    public AzureBlobObjectStore(
        PantsCloudProviderConfiguration.AzureBlob configuration,
        string prefix,
        HttpClient httpClient,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (timeout < TimeSpan.FromMilliseconds(1))
        {
            throw PantsException.InvalidArgument("Azure Blob operation timeout must be at least one millisecond.");
        }

        _prefix = NormalizePrefix(prefix);
        _timeout = timeout;
        var resolution = AzureCredentialResolver.Resolve(
            configuration,
            httpClient,
            timeout);
        _account = resolution.Account;
        _credential = resolution.Credential;
        _containerEndpoint = new Uri(
            $"{resolution.Endpoint.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(configuration.Container)}/",
            UriKind.Absolute);
    }

    public async ValueTask<CloudObject?> GetAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        using var response = await SendReadAsync(
            token => CreateGetRequestAsync(objectKey, token),
            cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var version = response.Headers.ETag?.Tag ??
            throw new PantsIOException("Azure Blob GET response did not include an ETag.");
        return new CloudObject(data, version);
    }

    public async ValueTask<CloudObjectMetadata?> HeadAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        using var response = await SendReadAsync(
            token => CreateHeadRequestAsync(objectKey, token),
            cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var size = response.Content.Headers.ContentLength is { } contentLength && contentLength >= 0
            ? checked((ulong)contentLength)
            : throw new PantsIOException(
                "Azure Blob HEAD response did not include a valid Content-Length.");
        var etag = response.Headers.ETag?.Tag ??
            throw new PantsIOException("Azure Blob HEAD response did not include an ETag.");
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
            token => CreatePutRequestAsync(objectKey, data, condition, token),
            cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
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
            cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        var document = CloudProviderXml.ParseList(body, "EnumerationResults", "Azure Blob");
        var objectKeys = document.Descendants()
            .Where(static element => element.Name.LocalName == "Name")
            .Select(element => StripConfiguredPrefix(element.Value))
            .ToArray();
        var nextMarker = document.Descendants()
            .FirstOrDefault(static element => element.Name.LocalName == "NextMarker")?
            .Value;
        return new CloudObjectListPage(
            objectKeys,
            string.IsNullOrEmpty(nextMarker) ? null : nextMarker);
    }

    public async ValueTask<CloudObjectDeleteOutcome> DeleteAsync(
        string objectKey,
        CloudObjectDeleteCondition condition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);
        using var response = await SendMutationAsync(
            token => CreateDeleteRequestAsync(objectKey, condition, token),
            cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return CloudObjectDeleteOutcome.NotFound;
        }

        if (response.StatusCode == HttpStatusCode.PreconditionFailed &&
            condition is CloudObjectDeleteCondition.IfVersion &&
            await HasAzurePredicateFailureAsync(response, cancellationToken).ConfigureAwait(false))
        {
            return CloudObjectDeleteOutcome.ConditionNotMet;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return CloudObjectDeleteOutcome.Deleted;
    }

    async ValueTask<HttpRequestMessage> CreateGetRequestAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BuildObjectUri(objectKey));
        await PrepareRequestAsync(request, cancellationToken).ConfigureAwait(false);
        return request;
    }

    async ValueTask<HttpRequestMessage> CreateHeadRequestAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Head, BuildObjectUri(objectKey));
        await PrepareRequestAsync(request, cancellationToken).ConfigureAwait(false);
        return request;
    }

    async ValueTask<HttpRequestMessage> CreatePutRequestAsync(
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition condition,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, BuildObjectUri(objectKey))
        {
            Content = new ByteArrayContent(data.ToArray())
        };
        request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        switch (condition)
        {
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

        await PrepareRequestAsync(request, cancellationToken).ConfigureAwait(false);
        return request;
    }

    async ValueTask<HttpRequestMessage> CreateListRequestAsync(
        string fullPrefix,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        var builder = new UriBuilder(_containerEndpoint.AbsoluteUri.TrimEnd('/'));
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("restype", "container"),
            new("comp", "list"),
            new("prefix", fullPrefix)
        };
        if (continuationToken is not null)
        {
            parameters.Add(new("marker", continuationToken));
        }

        builder.Query = string.Join(
            '&',
            parameters.Select(static parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
        var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        await PrepareRequestAsync(request, cancellationToken).ConfigureAwait(false);
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

        await PrepareRequestAsync(request, cancellationToken).ConfigureAwait(false);
        return request;
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
                        $"Azure Blob mutation outcome is indeterminate after HTTP {(int)statusCode}.");
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
                        "Azure Blob mutation outcome is indeterminate after its request deadline elapsed.",
                        exception);
                }

                throw new PantsTimeoutException("Azure Blob operation exceeded its deadline.", exception);
            }
            catch (HttpRequestException exception) when (!retryTransientFailures)
            {
                throw new PantsIOException(
                    "Azure Blob mutation outcome is indeterminate after a transport failure.",
                    exception);
            }
            catch (HttpRequestException exception) when (attempt >= MaximumAttempts)
            {
                throw new PantsIOException(
                    "Azure Blob transport failed after bounded retries.",
                    exception);
            }
            catch (HttpRequestException)
            {
            }

            await Task.Yield();
        }
    }

    static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    async ValueTask PrepareRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("x-ms-version", ServiceVersion);
        request.Headers.TryAddWithoutValidation(
            "x-ms-date",
            DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture));
        switch (_credential.Kind)
        {
            case AzureResolvedCredentialKind.Sas:
                request.RequestUri = AppendQuery(request.RequestUri!, _credential.Secret!);
                break;
            case AzureResolvedCredentialKind.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    await _credential.TokenProvider!.GetTokenAsync(cancellationToken)
                        .ConfigureAwait(false));
                break;
            case AzureResolvedCredentialKind.SharedKey:
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "SharedKey",
                    $"{_account}:{CreateSharedKeySignature(request, _credential.Secret!)}");
                break;
            default:
                throw new PantsInternalException("Unknown Azure Blob credential kind.");
        }
    }

    string CreateSharedKeySignature(HttpRequestMessage request, string accountKey)
    {
        var stringToSign = CreateSharedKeyStringToSign(request, _account);
        byte[] key;
        try
        {
            key = Convert.FromBase64String(accountKey);
        }
        catch (FormatException exception)
        {
            throw new PantsInvalidArgumentException(
                "Azure Storage shared key must be base64 encoded.",
                exception);
        }

        return Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)));
    }

    internal static string CreateSharedKeyStringToSign(
        HttpRequestMessage request,
        string account)
    {
        var contentLength = request.Content?.Headers.ContentLength is > 0 and var length
            ? length.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        var canonicalHeaders = string.Join(
            '\n',
            request.Headers
                .Where(static header => header.Key.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static header => header.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static header =>
                    $"{header.Key.ToLowerInvariant()}:{string.Join(',', header.Value).Trim()}")) + "\n";
        var canonicalResource = $"/{account}{request.RequestUri!.AbsolutePath}";
        foreach (var query in request.RequestUri.Query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries)
                     .Select(static pair => pair.Split('=', 2))
                     .GroupBy(
                         static pair => Uri.UnescapeDataString(pair[0]).ToLowerInvariant(),
                         StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            canonicalResource +=
                $"\n{query.Key}:{string.Join(',', query.Select(static pair => Uri.UnescapeDataString(pair.ElementAtOrDefault(1) ?? string.Empty)).Order(StringComparer.Ordinal))}";
        }

        return string.Join(
            '\n',
            request.Method.Method,
            GetSharedKeyHeaderValue(request, "Content-Encoding"),
            GetSharedKeyHeaderValue(request, "Content-Language"),
            contentLength,
            GetSharedKeyHeaderValue(request, "Content-MD5"),
            GetSharedKeyHeaderValue(request, "Content-Type"),
            GetSharedKeyHeaderValue(request, "Date"),
            GetSharedKeyHeaderValue(request, "If-Modified-Since"),
            GetSharedKeyHeaderValue(request, "If-Match"),
            GetSharedKeyHeaderValue(request, "If-None-Match"),
            GetSharedKeyHeaderValue(request, "If-Unmodified-Since"),
            GetSharedKeyHeaderValue(request, "Range"),
            canonicalHeaders + canonicalResource);
    }

    static string GetSharedKeyHeaderValue(HttpRequestMessage request, string name)
    {
        if (request.Headers.TryGetValues(name, out var values) ||
            request.Content?.Headers.TryGetValues(name, out values) == true)
        {
            return string.Join(',', values);
        }

        return string.Empty;
    }

    Uri BuildObjectUri(string objectKey)
    {
        var normalized = NormalizeObjectKey(objectKey);
        var combined = string.IsNullOrEmpty(_prefix) ? normalized : $"{_prefix}/{normalized}";
        var escaped = string.Join('/', combined.Split('/').Select(Uri.EscapeDataString));
        return new Uri(_containerEndpoint, escaped);
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
                $"Azure Blob LIST returned object '{objectKey}' outside the configured prefix.");
    }

    static async ValueTask<bool> HasAzurePredicateFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var headerCode = response.Headers.TryGetValues("x-ms-error-code", out var values)
            ? values.FirstOrDefault()
            : null;
        var body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        var code = string.IsNullOrWhiteSpace(headerCode)
            ? CloudProviderXml.TryReadElementValue(body, "Code")
            : headerCode;
        return code is not null &&
            (code.Equals("ConditionNotMet", StringComparison.OrdinalIgnoreCase) ||
             code.Equals("TargetConditionNotMet", StringComparison.OrdinalIgnoreCase));
    }

    static string RequireVersion(string version) =>
        string.IsNullOrWhiteSpace(version)
            ? throw new PantsInvalidArgumentException(
                "A non-empty cloud object version is required for conditional deletion.")
            : version;

    static Uri AppendQuery(Uri uri, string query)
    {
        var builder = new UriBuilder(uri);
        var token = query.TrimStart('?');
        builder.Query = string.IsNullOrEmpty(builder.Query)
            ? token
            : $"{builder.Query.TrimStart('?')}&{token}";
        return builder.Uri;
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
        if (string.IsNullOrWhiteSpace(objectKey) ||
            objectKey.StartsWith('/') ||
            objectKey.EndsWith('/') ||
            objectKey.Contains('\\') ||
            objectKey.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new PantsInvalidArgumentException("Cloud object key is unsafe or empty.");
        }

        return objectKey;
    }

    static async ValueTask EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var providerRequestId = response.Headers.TryGetValues(
            "x-ms-request-id",
            out var values)
                ? values.FirstOrDefault() ?? "unavailable"
                : "unavailable";
        _ = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new PantsIOException(
            $"Azure Blob request failed with HTTP {(int)response.StatusCode}; request ID {providerRequestId}.");
    }
}
