using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Pants;

internal sealed class AzureBlobObjectStore : ICloudObjectStore
{
    const int MaximumAttempts = 3;
    const string ServiceVersion = "2024-11-04";
    private readonly HttpClient _httpClient;
    private readonly Uri _containerEndpoint;
    private readonly string _account;
    private readonly string _prefix;
    private readonly TimeSpan _timeout;
    private readonly Credential _credential;

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

        _account = configuration.Account;
        _prefix = NormalizePrefix(prefix);
        _timeout = timeout;
        _credential = ResolveCredential(configuration.Credential);
        Uri accountEndpoint = configuration.Endpoint ??
            new Uri($"https://{configuration.Account}.blob.core.windows.net", UriKind.Absolute);
        _containerEndpoint = new Uri(
            $"{accountEndpoint.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(configuration.Container)}/",
            UriKind.Absolute);
    }

    public async ValueTask<CloudObject?> GetAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => CreateGetRequest(objectKey),
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

    public async ValueTask<bool> PutAsync(
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition condition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);
        using var response = await SendAsync(
            () => CreatePutRequest(objectKey, data, condition),
            cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }

    HttpRequestMessage CreateGetRequest(string objectKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BuildObjectUri(objectKey));
        PrepareRequest(request);
        return request;
    }

    HttpRequestMessage CreatePutRequest(
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition condition)
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

        PrepareRequest(request);
        return request;
    }

    async ValueTask<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        for (var attempt = 1; ; attempt++)
        {
            using var request = requestFactory();
            try
            {
                var response = await _httpClient.SendAsync(
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
                throw new PantsTimeoutException("Azure Blob operation exceeded its deadline.", exception);
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

    private void PrepareRequest(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("x-ms-version", ServiceVersion);
        request.Headers.TryAddWithoutValidation(
            "x-ms-date",
            DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture));
        switch (_credential.Kind)
        {
            case CredentialKind.Sas:
                request.RequestUri = AppendQuery(request.RequestUri!, _credential.Secret);
                break;
            case CredentialKind.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    _credential.Secret);
                break;
            case CredentialKind.SharedKey:
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "SharedKey",
                    $"{_account}:{CreateSharedKeySignature(request, _credential.Secret)}");
                break;
            default:
                throw new PantsInternalException("Unknown Azure Blob credential kind.");
        }
    }

    private string CreateSharedKeySignature(HttpRequestMessage request, string accountKey)
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
            string.Empty,
            string.Empty,
            contentLength,
            string.Empty,
            request.Content?.Headers.ContentType?.ToString() ?? string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            canonicalHeaders + canonicalResource);
    }

    private Uri BuildObjectUri(string objectKey)
    {
        string normalized = NormalizeObjectKey(objectKey);
        string combined = string.IsNullOrEmpty(_prefix) ? normalized : $"{_prefix}/{normalized}";
        string escaped = string.Join('/', combined.Split('/').Select(Uri.EscapeDataString));
        return new Uri(_containerEndpoint, escaped);
    }

    private static Uri AppendQuery(Uri uri, string query)
    {
        var builder = new UriBuilder(uri);
        string token = query.TrimStart('?');
        builder.Query = string.IsNullOrEmpty(builder.Query)
            ? token
            : $"{builder.Query.TrimStart('?')}&{token}";
        return builder.Uri;
    }

    private static Credential ResolveCredential(PantsAzureCredentialSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source switch
        {
            PantsAzureCredentialSource.SharedKey sharedKey =>
                new Credential(CredentialKind.SharedKey, RequireSecret(sharedKey.AccountKey)),
            PantsAzureCredentialSource.SasToken sas =>
                new Credential(CredentialKind.Sas, RequireSecret(sas.Token)),
            PantsAzureCredentialSource.ConnectionString connection =>
                ResolveConnectionString(connection.Value),
            PantsAzureCredentialSource.StorageEnvironment => ResolveStorageEnvironment(),
            _ => ResolveBearerEnvironment(source)
        };
    }

    private static Credential ResolveConnectionString(string value)
    {
        Dictionary<string, string> fields = value
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => part.Split('=', 2))
            .Where(static pair => pair.Length == 2)
            .ToDictionary(static pair => pair[0], static pair => pair[1], StringComparer.OrdinalIgnoreCase);
        if (fields.TryGetValue("SharedAccessSignature", out string? sas))
        {
            return new Credential(CredentialKind.Sas, RequireSecret(sas));
        }

        if (fields.TryGetValue("AccountKey", out string? key))
        {
            return new Credential(CredentialKind.SharedKey, RequireSecret(key));
        }

        throw new PantsInvalidArgumentException(
            "Azure Storage connection string must contain AccountKey or SharedAccessSignature.");
    }

    private static Credential ResolveStorageEnvironment()
    {
        string? connectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return ResolveConnectionString(connectionString);
        }

        string? sas = Environment.GetEnvironmentVariable("AZURE_STORAGE_SAS_TOKEN");
        if (!string.IsNullOrWhiteSpace(sas))
        {
            return new Credential(CredentialKind.Sas, sas);
        }

        string? key = Environment.GetEnvironmentVariable("AZURE_STORAGE_ACCOUNT_KEY");
        return !string.IsNullOrWhiteSpace(key)
            ? new Credential(CredentialKind.SharedKey, key)
            : throw new PantsInvalidArgumentException(
                "Azure Storage environment credentials are unavailable.");
    }

    private static Credential ResolveBearerEnvironment(PantsAzureCredentialSource source)
    {
        string? token = Environment.GetEnvironmentVariable("AZURE_STORAGE_BEARER_TOKEN");
        return !string.IsNullOrWhiteSpace(token)
            ? new Credential(CredentialKind.Bearer, token)
            : throw new PantsNotSupportedException(
                $"Azure credential source '{source.GetType().Name}' requires a token provider. " +
                "Set AZURE_STORAGE_BEARER_TOKEN for direct-HTTP operation.");
    }

    private static string RequireSecret(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new PantsInvalidArgumentException("Azure credential value must not be empty.")
            : value;

    private static string NormalizePrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        string normalized = prefix.Trim('/');
        return string.IsNullOrEmpty(normalized) ? string.Empty : NormalizeObjectKey(normalized);
    }

    private static string NormalizeObjectKey(string objectKey)
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

    private enum CredentialKind
    {
        SharedKey,
        Sas,
        Bearer
    }

    private sealed record Credential(CredentialKind Kind, string Secret);

    private static async ValueTask EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string providerRequestId = response.Headers.TryGetValues(
            "x-ms-request-id",
            out IEnumerable<string>? values)
                ? values.FirstOrDefault() ?? "unavailable"
                : "unavailable";
        _ = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new PantsIOException(
            $"Azure Blob request failed with HTTP {(int)response.StatusCode}; request ID {providerRequestId}.");
    }
}
