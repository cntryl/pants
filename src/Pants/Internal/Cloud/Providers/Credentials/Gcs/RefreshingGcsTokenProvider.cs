using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pants;

internal sealed class RefreshingGcsTokenProvider : IGcsTokenProvider, IDisposable
{
    const string StorageScope =
        "https://www.googleapis.com/auth/devstorage.full_control";
    readonly HttpClient _httpClient;
    readonly PantsGcsCredentialSource _source;
    readonly TimeSpan _timeout;
    readonly SemaphoreSlim _gate = new(1, 1);
    GcsAccessToken? _cached;
    int _disposed;

    public RefreshingGcsTokenProvider(
        HttpClient httpClient,
        PantsGcsCredentialSource source,
        TimeSpan timeout)
    {
        _httpClient = httpClient;
        _source = source;
        _timeout = timeout;
    }

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_cached is { } cached && !cached.RequiresRefresh(DateTimeOffset.UtcNow))
        {
            return cached.Value;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is { } current && !current.RequiresRefresh(DateTimeOffset.UtcNow))
            {
                return current.Value;
            }

            _cached = await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return _cached.Value;
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

    ValueTask<GcsAccessToken> RefreshAsync(CancellationToken cancellationToken) =>
        _source switch
        {
            PantsGcsCredentialSource.ApplicationDefault =>
                RefreshApplicationDefaultAsync(cancellationToken),
            PantsGcsCredentialSource.ServiceAccountJsonFile file =>
                RefreshCredentialFileAsync(file.Path, cancellationToken),
            PantsGcsCredentialSource.AuthorizedUserJsonFile file =>
                RefreshCredentialFileAsync(file.Path, cancellationToken),
            PantsGcsCredentialSource.MetadataServer =>
                RefreshMetadataAsync(cancellationToken),
            _ => throw new PantsNotSupportedException(
                "The GCS token credential source is unsupported.")
        };

    ValueTask<GcsAccessToken> RefreshApplicationDefaultAsync(
        CancellationToken cancellationToken)
    {
        var configuredPath = Environment.GetEnvironmentVariable(
            "GOOGLE_APPLICATION_CREDENTIALS");
        if (configuredPath is not null)
        {
            return RefreshCredentialFileAsync(configuredPath, cancellationToken);
        }

        var defaultPath = DefaultApplicationCredentialPath();
        return defaultPath is not null && File.Exists(defaultPath)
            ? RefreshCredentialFileAsync(defaultPath, cancellationToken)
            : RefreshMetadataAsync(cancellationToken);
    }

    async ValueTask<GcsAccessToken> RefreshCredentialFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var document = await ReadCredentialFileAsync(path, cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        return GetRequiredString(root, "type") switch
        {
            "service_account" => await RefreshServiceAccountAsync(
                root,
                cancellationToken).ConfigureAwait(false),
            "authorized_user" => await RefreshAuthorizedUserAsync(
                root,
                cancellationToken).ConfigureAwait(false),
            "external_account" => await new GcsExternalAccountTokenProvider(
                _httpClient,
                _timeout).FetchAsync(root, cancellationToken).ConfigureAwait(false),
            var type => throw new PantsInvalidArgumentException(
                $"Google application-default credential type '{type}' is unsupported.")
        };
    }

    async ValueTask<GcsAccessToken> RefreshServiceAccountAsync(
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var email = GetRequiredString(root, "client_email");
        var privateKey = GetRequiredString(root, "private_key");
        var tokenUri = GetOptionalString(root, "token_uri") ??
            "https://oauth2.googleapis.com/token";
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url(
            Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var claims = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = email,
            ["scope"] = StorageScope,
            ["aud"] = tokenUri,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds()
        });
        var unsigned = $"{header}.{Base64Url(Encoding.UTF8.GetBytes(claims))}";
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(privateKey);
        }
        catch (ArgumentException exception)
        {
            throw new PantsInvalidArgumentException(
                "GCS service-account private key is invalid.",
                exception);
        }
        catch (CryptographicException exception)
        {
            throw new PantsInvalidArgumentException(
                "GCS service-account private key is invalid.",
                exception);
        }

        var assertion = $"{unsigned}.{Base64Url(rsa.SignData(
            Encoding.UTF8.GetBytes(unsigned),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1))}";
        return await RequestTokenAsync(
            new Uri(tokenUri, UriKind.Absolute),
            new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion
            },
            cancellationToken).ConfigureAwait(false);
    }

    ValueTask<GcsAccessToken> RefreshAuthorizedUserAsync(
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var tokenUri = GetOptionalString(root, "token_uri") ??
            "https://oauth2.googleapis.com/token";
        return RequestTokenAsync(
            new Uri(tokenUri, UriKind.Absolute),
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = GetRequiredString(root, "client_id"),
                ["client_secret"] = GetRequiredString(root, "client_secret"),
                ["refresh_token"] = GetRequiredString(root, "refresh_token")
            },
            cancellationToken);
    }

    async ValueTask<GcsAccessToken> RefreshMetadataAsync(
        CancellationToken cancellationToken)
    {
        var host = Environment.GetEnvironmentVariable("GCE_METADATA_HOST") ??
            "metadata.google.internal";
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"http://{host.TrimEnd('/')}/computeMetadata/v1/instance/service-accounts/default/token");
        request.Headers.TryAddWithoutValidation("Metadata-Flavor", "Google");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "GCS metadata token request");
        return await ParseTokenResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<GcsAccessToken> RequestTokenAsync(
        Uri uri,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/x-www-form-urlencoded");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "GCS token request");
        return await ParseTokenResponseAsync(response, cancellationToken).ConfigureAwait(false);
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
                "GCS token refresh exceeded its deadline.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new PantsIOException("GCS token refresh transport failed.", exception);
        }
    }

    internal static async ValueTask<GcsAccessToken> ParseTokenResponseAsync(
        HttpResponseMessage response,
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
            throw new PantsIOException("GCS token response JSON is malformed.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new PantsIOException(
                    "GCS token response must be a JSON object.");
            }

            var token = GetOptionalString(root, "access_token");
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new PantsIOException(
                    "GCS token response omitted a non-empty access_token.");
            }

            if (!root.TryGetProperty("expires_in", out var expires) ||
                expires.ValueKind != JsonValueKind.Number ||
                !expires.TryGetUInt64(out var lifetime) ||
                lifetime == 0 ||
                lifetime > long.MaxValue)
            {
                throw new PantsIOException(
                    "GCS token response must include a positive expires_in.");
            }

            var now = DateTimeOffset.UtcNow;
            DateTimeOffset expiresAt;
            try
            {
                expiresAt = now.AddSeconds((long)lifetime);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new PantsIOException(
                    "GCS token response expires_in did not yield a future expiry.",
                    exception);
            }

            if (expiresAt <= now)
            {
                throw new PantsIOException(
                    "GCS token response expires_in did not yield a future expiry.");
            }

            return new GcsAccessToken(token, expiresAt);
        }
    }

    internal static string GetRequiredString(JsonElement element, string name) =>
        GetOptionalString(element, name) ??
        throw new PantsInvalidArgumentException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Google credential field '{name}' is missing."));

    internal static string? GetOptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text &&
        !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    static async ValueTask<JsonDocument> ReadCredentialFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new PantsInvalidArgumentException(
                "Google credential JSON file path must not be empty.");
        }

        try
        {
            return JsonDocument.Parse(
                await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (FileNotFoundException exception)
        {
            throw new PantsIOException(
                "Google credential JSON file does not exist.",
                exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new PantsIOException(
                "Google credential JSON file does not exist.",
                exception);
        }
        catch (IOException exception)
        {
            throw new PantsIOException(
                "Google credential JSON file could not be read.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new PantsIOException(
                "Google credential JSON file could not be read.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new PantsInvalidArgumentException(
                "Google credential JSON is malformed.",
                exception);
        }
    }

    static string? DefaultApplicationCredentialPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrWhiteSpace(appData)
                ? null
                : Path.Combine(
                    appData,
                    "gcloud",
                    "application_default_credentials.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? null
            : Path.Combine(
                home,
                ".config",
                "gcloud",
                "application_default_credentials.json");
    }

    static string Base64Url(ReadOnlySpan<byte> value) => Convert
        .ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new PantsIOException(
                $"{operation} failed with HTTP {(int)response.StatusCode}.");
        }
    }
}
