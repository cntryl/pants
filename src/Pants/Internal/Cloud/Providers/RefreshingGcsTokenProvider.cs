using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pants;

internal sealed class RefreshingGcsTokenProvider : IGcsTokenProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly PantsGcsCredentialSource _source;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private long _expiresAtUtcTicks;
    private int _disposed;

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
        if (_token is { } cached && DateTimeOffset.UtcNow.UtcTicks + TimeSpan.FromMinutes(1).Ticks <
            Volatile.Read(ref _expiresAtUtcTicks))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is { } current && DateTimeOffset.UtcNow.UtcTicks + TimeSpan.FromMinutes(1).Ticks <
                Volatile.Read(ref _expiresAtUtcTicks))
            {
                return current;
            }

            (string token, TimeSpan lifetime) = await RefreshAsync(cancellationToken)
                .ConfigureAwait(false);
            _token = token;
            Volatile.Write(ref _expiresAtUtcTicks, (DateTimeOffset.UtcNow + lifetime).UtcTicks);
            return token;
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

    private ValueTask<(string Token, TimeSpan Lifetime)> RefreshAsync(
        CancellationToken cancellationToken) => _source switch
        {
            PantsGcsCredentialSource.ApplicationDefault => RefreshApplicationDefaultAsync(
                cancellationToken),
            PantsGcsCredentialSource.ServiceAccountJsonFile file => RefreshServiceAccountAsync(
                file.Path,
                cancellationToken),
            PantsGcsCredentialSource.AuthorizedUserJsonFile file => RefreshAuthorizedUserAsync(
                file.Path,
                cancellationToken),
            PantsGcsCredentialSource.MetadataServer => RefreshMetadataAsync(cancellationToken),
            _ => throw new PantsNotSupportedException("The GCS token credential source is unsupported.")
        };

    private ValueTask<(string Token, TimeSpan Lifetime)> RefreshApplicationDefaultAsync(
        CancellationToken cancellationToken)
    {
        string? directToken = Environment.GetEnvironmentVariable("GOOGLE_OAUTH_ACCESS_TOKEN");
        if (!string.IsNullOrWhiteSpace(directToken))
        {
            return ValueTask.FromResult((directToken, TimeSpan.FromHours(1)));
        }

        string? path = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new PantsInvalidArgumentException("Google application-default credentials are unavailable.");
        }

        using JsonDocument document = ReadCredentialFile(path);
        string type = GetRequiredString(document.RootElement, "type");
        return type switch
        {
            "service_account" => RefreshServiceAccountAsync(path, cancellationToken),
            "authorized_user" => RefreshAuthorizedUserAsync(path, cancellationToken),
            _ => throw new PantsInvalidArgumentException(
                $"Google application-default credential type '{type}' is unsupported.")
        };
    }

    private async ValueTask<(string Token, TimeSpan Lifetime)> RefreshServiceAccountAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = ReadCredentialFile(path);
        JsonElement root = document.RootElement;
        string email = GetRequiredString(root, "client_email");
        string privateKey = GetRequiredString(root, "private_key");
        string tokenUri = GetRequiredString(root, "token_uri");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        string claims = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = email,
            ["scope"] = "https://www.googleapis.com/auth/devstorage.read_write",
            ["aud"] = tokenUri,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds()
        });
        string unsigned = $"{header}.{Base64Url(Encoding.UTF8.GetBytes(claims))}";
        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(privateKey);
        string assertion = $"{unsigned}.{Base64Url(rsa.SignData(
            Encoding.UTF8.GetBytes(unsigned),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1))}";
        return await RequestTokenAsync(
            new Uri(tokenUri),
            new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<(string Token, TimeSpan Lifetime)> RefreshAuthorizedUserAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = ReadCredentialFile(path);
        JsonElement root = document.RootElement;
        string tokenUri = root.TryGetProperty("token_uri", out JsonElement uri)
            ? uri.GetString() ?? "https://oauth2.googleapis.com/token"
            : "https://oauth2.googleapis.com/token";
        return await RequestTokenAsync(
            new Uri(tokenUri),
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = GetRequiredString(root, "client_id"),
                ["client_secret"] = GetRequiredString(root, "client_secret"),
                ["refresh_token"] = GetRequiredString(root, "refresh_token")
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<(string Token, TimeSpan Lifetime)> RefreshMetadataAsync(
        CancellationToken cancellationToken)
    {
        string host = Environment.GetEnvironmentVariable("GCE_METADATA_HOST") ??
            "metadata.google.internal";
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"http://{host}/computeMetadata/v1/instance/service-accounts/default/token");
        request.Headers.TryAddWithoutValidation("Metadata-Flavor", "Google");
        using HttpResponseMessage response = await SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return await ParseTokenResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<(string Token, TimeSpan Lifetime)> RequestTokenAsync(
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
        using HttpResponseMessage response = await SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return await ParseTokenResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            return await _httpClient.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PantsTimeoutException("GCS token refresh exceeded its deadline.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new PantsIOException("GCS token refresh transport failed.", exception);
        }
    }

    private static async ValueTask<(string Token, TimeSpan Lifetime)> ParseTokenResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new PantsIOException(
                $"GCS token refresh failed with HTTP {(int)response.StatusCode}.");
        }

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        string token = GetRequiredString(document.RootElement, "access_token");
        int lifetimeSeconds = document.RootElement.TryGetProperty("expires_in", out JsonElement expires) &&
            expires.TryGetInt32(out int seconds)
                ? seconds
                : 3600;
        return (token, TimeSpan.FromSeconds(Math.Max(1, lifetimeSeconds)));
    }

    private static JsonDocument ReadCredentialFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new PantsInvalidArgumentException("Google credential JSON file does not exist.");
        }

        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch (JsonException exception)
        {
            throw new PantsInvalidArgumentException("Google credential JSON is malformed.", exception);
        }
    }

    private static string GetRequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new PantsInvalidArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Google credential field '{name}' is missing."));

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
