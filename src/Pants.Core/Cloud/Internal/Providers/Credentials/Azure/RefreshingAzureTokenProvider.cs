using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Cntryl.Pants.Cloud.Internal.Providers.Credentials.Azure;

sealed class RefreshingAzureTokenProvider : IAzureTokenProvider, IDisposable
{
    readonly Uri _blobEndpoint;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly HttpClient _httpClient;
    readonly PantsAzureCredentialSource _source;
    readonly TimeProvider _timeProvider;
    readonly TimeSpan _timeout;
    CachedAzureToken? _cached;
    int _disposed;

    public RefreshingAzureTokenProvider(
        PantsAzureCredentialSource source,
        Uri blobEndpoint,
        HttpClient httpClient,
        TimeSpan timeout,
        TimeProvider timeProvider)
    {
        _source = source is PantsAzureCredentialSource.LightweightDefaultChain
            ? SelectDefaultChainSource()
            : source;
        _blobEndpoint = blobEndpoint;
        _httpClient = httpClient;
        _timeout = timeout;
        _timeProvider = timeProvider;
    }

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_cached is { } cached && !cached.RequiresRefresh(_timeProvider.GetUtcNow()))
        {
            return cached.AccessToken;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is { } current && !current.RequiresRefresh(_timeProvider.GetUtcNow()))
            {
                return current.AccessToken;
            }

            _cached = await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return _cached.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _gate.Dispose();
        }
    }

    ValueTask<CachedAzureToken> RefreshAsync(CancellationToken cancellationToken) =>
        _source switch
        {
            PantsAzureCredentialSource.EnvironmentClientSecret =>
                RefreshClientSecretAsync(cancellationToken),
            PantsAzureCredentialSource.WorkloadIdentity workload =>
                RefreshWorkloadIdentityAsync(workload, cancellationToken),
            PantsAzureCredentialSource.ManagedIdentity managed =>
                RefreshManagedIdentityAsync(managed, cancellationToken),
            _ => throw new PantsNotSupportedException(
                "The Azure bearer-token credential source is unsupported.")
        };

    ValueTask<CachedAzureToken> RefreshClientSecretAsync(
        CancellationToken cancellationToken) => RequestOAuthTokenAsync(
        RequireEnvironment("AZURE_TENANT_ID"),
        new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = RequireEnvironment("AZURE_CLIENT_ID"),
            ["client_secret"] = RequireEnvironment("AZURE_CLIENT_SECRET"),
            ["scope"] = "https://storage.azure.com/.default"
        },
        cancellationToken);

    async ValueTask<CachedAzureToken> RefreshWorkloadIdentityAsync(
        PantsAzureCredentialSource.WorkloadIdentity source,
        CancellationToken cancellationToken)
    {
        var tenant = FirstNonempty(source.TenantId, Environment.GetEnvironmentVariable("AZURE_TENANT_ID")) ??
                     throw new PantsInvalidArgumentException("Missing AZURE_TENANT_ID.");
        var client = FirstNonempty(source.ClientId, Environment.GetEnvironmentVariable("AZURE_CLIENT_ID")) ??
                     throw new PantsInvalidArgumentException("Missing AZURE_CLIENT_ID.");
        var tokenPath = FirstNonempty(
                            source.TokenFile,
                            Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE")) ??
                        throw new PantsInvalidArgumentException("Missing AZURE_FEDERATED_TOKEN_FILE.");
        string assertion;
        try
        {
            assertion = (await File.ReadAllTextAsync(tokenPath, cancellationToken)
                .ConfigureAwait(false)).Trim();
        }
        catch (IOException exception)
        {
            throw new PantsIOException(
                "Azure federated token file could not be read.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new PantsIOException(
                "Azure federated token file could not be read.",
                exception);
        }

        if (assertion.Length == 0)
        {
            throw new PantsInvalidArgumentException(
                "Azure federated token file is empty.");
        }

        return await RequestOAuthTokenAsync(
            tenant,
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = client,
                ["client_assertion_type"] =
                    "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = assertion,
                ["scope"] = "https://storage.azure.com/.default"
            },
            cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<CachedAzureToken> RefreshManagedIdentityAsync(
        PantsAzureCredentialSource.ManagedIdentity source,
        CancellationToken cancellationToken)
    {
        var clientId = FirstNonempty(
            source.ClientId,
            Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"));
        var identityEndpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");
        var msiEndpoint = Environment.GetEnvironmentVariable("MSI_ENDPOINT");
        var endpoint = !string.IsNullOrWhiteSpace(identityEndpoint)
            ? ParseManagedIdentityEndpoint(identityEndpoint, "IDENTITY_ENDPOINT")
            : !string.IsNullOrWhiteSpace(msiEndpoint)
                ? ParseManagedIdentityEndpoint(msiEndpoint, "MSI_ENDPOINT")
                : new Uri("http://169.254.169.254/metadata/identity/oauth2/token");
        var apiVersion = !string.IsNullOrWhiteSpace(msiEndpoint) &&
                         string.IsNullOrWhiteSpace(identityEndpoint)
            ? "2017-09-01"
            : "2019-08-01";
        var builder = new UriBuilder(endpoint);
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("api-version", apiVersion),
            new("resource", "https://storage.azure.com/")
        };
        if (clientId is not null)
        {
            parameters.Add(new KeyValuePair<string, string>("client_id", clientId));
        }

        builder.Query = string.Join(
            '&',
            parameters.Select(static parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        if (!string.IsNullOrWhiteSpace(identityEndpoint))
        {
            request.Headers.TryAddWithoutValidation(
                "X-IDENTITY-HEADER",
                RequireEnvironment("IDENTITY_HEADER"));
        }
        else if (!string.IsNullOrWhiteSpace(msiEndpoint))
        {
            request.Headers.TryAddWithoutValidation(
                "Secret",
                RequireEnvironment("MSI_SECRET"));
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Metadata", "true");
        }

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "Azure managed identity token request");
        return await ParseTokenResponseAsync(
            response,
            true,
            cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<CachedAzureToken> RequestOAuthTokenAsync(
        string tenant,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        var authority = ResolveAuthority();
        var tokenUri = new Uri(
            $"{authority.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token",
            UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/x-www-form-urlencoded");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "Azure OAuth token request");
        return await ParseTokenResponseAsync(
            response,
            false,
            cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PantsTimeoutException(
                "Azure credential refresh exceeded its deadline.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new PantsIOException("Azure credential transport failed.", exception);
        }
    }

    Uri ResolveAuthority()
    {
        var configured = Environment.GetEnvironmentVariable("AZURE_AUTHORITY_HOST");
        if (configured is not null)
        {
            return ValidateAuthority(configured);
        }

        var authority = _blobEndpoint.Host switch
        {
            var host when host.EndsWith(
                ".blob.core.usgovcloudapi.net",
                StringComparison.OrdinalIgnoreCase) => "https://login.microsoftonline.us",
            var host when host.EndsWith(
                ".blob.core.chinacloudapi.cn",
                StringComparison.OrdinalIgnoreCase) => "https://login.chinacloudapi.cn",
            _ => "https://login.microsoftonline.com"
        };
        return new Uri(authority, UriKind.Absolute);
    }

    static Uri ValidateAuthority(string value)
    {
        if (value != value.Trim() ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length > 0 ||
            uri.AbsolutePath != "/" ||
            uri.Query.Length > 0 ||
            uri.Fragment.Length > 0)
        {
            throw new PantsInvalidArgumentException(
                "AZURE_AUTHORITY_HOST must be an absolute HTTPS origin.");
        }

        return uri;
    }

    async ValueTask<CachedAzureToken> ParseTokenResponseAsync(
        HttpResponseMessage response,
        bool requireAbsoluteExpiry,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                await response.Content.ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (JsonException exception)
        {
            throw new PantsIOException("Azure token response JSON is malformed.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new PantsIOException(
                    "Azure token response must be a JSON object.");
            }

            var token = root.TryGetProperty("access_token", out var tokenElement) &&
                        tokenElement.ValueKind == JsonValueKind.String
                ? tokenElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new PantsIOException(
                    "Azure token response omitted a non-empty access_token.");
            }

            var now = _timeProvider.GetUtcNow();
            DateTimeOffset expiresAt;
            if (root.TryGetProperty("expires_on", out var absolute) &&
                TryReadPositiveInt64(absolute, out var seconds))
            {
                try
                {
                    expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    throw new PantsIOException(
                        "Azure token response expiry is invalid.",
                        exception);
                }
            }
            else if (!requireAbsoluteExpiry &&
                     root.TryGetProperty("expires_in", out var relative) &&
                     TryReadPositiveInt64(relative, out var lifetime) && lifetime > 0)
            {
                try
                {
                    expiresAt = now.AddSeconds(lifetime);
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    throw new PantsIOException(
                        "Azure token response expiry overflowed.",
                        exception);
                }
            }
            else
            {
                throw new PantsIOException(
                    "Azure token response omitted a valid expiry.");
            }

            if (expiresAt <= now)
            {
                throw new PantsIOException(
                    "Azure token response expiry is not in the future.");
            }

            return new CachedAzureToken(token, expiresAt);
        }
    }

    static PantsAzureCredentialSource SelectDefaultChainSource()
    {
        if (HasCompleteEnvironment(
                "AZURE_TENANT_ID",
                "AZURE_CLIENT_ID",
                "AZURE_CLIENT_SECRET"))
        {
            return new PantsAzureCredentialSource.EnvironmentClientSecret();
        }

        if (HasCompleteEnvironment(
                "AZURE_TENANT_ID",
                "AZURE_CLIENT_ID",
                "AZURE_FEDERATED_TOKEN_FILE"))
        {
            return new PantsAzureCredentialSource.WorkloadIdentity();
        }

        return new PantsAzureCredentialSource.ManagedIdentity();
    }

    static Uri ParseManagedIdentityEndpoint(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw new PantsInvalidArgumentException(
                $"{name} must be an absolute HTTP(S) URL.");
        }

        return endpoint;
    }

    static bool HasCompleteEnvironment(params string[] names) => names.All(static name => !string.IsNullOrWhiteSpace(
        Environment.GetEnvironmentVariable(name)));

    static string RequireEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new PantsInvalidArgumentException($"Missing or empty {name}.")
            : value;
    }

    static string? FirstNonempty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    static bool TryReadPositiveInt64(JsonElement value, out long result)
    {
        result = 0;
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt64(out result);
        }

        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(
                   value.GetString(),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out result);
    }

    static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new PantsIOException(
                $"{operation} failed with HTTP {(int)response.StatusCode}.");
        }
    }
}
