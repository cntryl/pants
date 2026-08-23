using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Cntryl.Pants;

internal sealed class GcsExternalAccountTokenProvider(
    HttpClient httpClient,
    TimeSpan timeout)
{
    static readonly string[] ImpersonationScopes =
    [
        "https://www.googleapis.com/auth/devstorage.full_control"
    ];
    readonly HttpClient _httpClient = httpClient;
    readonly TimeSpan _timeout = timeout;

    public async ValueTask<GcsAccessToken> FetchAsync(
        JsonElement credential,
        CancellationToken cancellationToken)
    {
        if (credential.TryGetProperty("client_id", out _) ||
            credential.TryGetProperty("client_secret", out _))
        {
            throw new PantsInvalidArgumentException(
                "GCS external-account client authentication is unsupported.");
        }

        if (credential.TryGetProperty("executable", out _))
        {
            throw new PantsInvalidArgumentException(
                "GCS executable external-account credentials are unsupported without process execution.");
        }

        var audience = RefreshingGcsTokenProvider.GetRequiredString(
            credential,
            "audience");
        var subjectTokenType = RefreshingGcsTokenProvider.GetRequiredString(
            credential,
            "subject_token_type");
        var tokenUrl = RefreshingGcsTokenProvider.GetRequiredString(
            credential,
            "token_url");
        var source = credential.TryGetProperty("credential_source", out var sourceElement)
            ? sourceElement
            : throw new PantsInvalidArgumentException(
                "GCS external-account ADC is missing credential_source.");
        if (source.ValueKind != JsonValueKind.Object)
        {
            throw new PantsInvalidArgumentException(
                "GCS external-account credential_source must be an object.");
        }

        var subjectToken = await ReadSubjectTokenAsync(source, cancellationToken)
            .ConfigureAwait(false);
        var impersonationUrl = RefreshingGcsTokenProvider.GetOptionalString(
            credential,
            "service_account_impersonation_url");
        var scope = impersonationUrl is null
            ? "https://www.googleapis.com/auth/devstorage.full_control"
            : "https://www.googleapis.com/auth/cloud-platform";
        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["audience"] = audience,
            ["scope"] = scope,
            ["requested_token_type"] =
                "urn:ietf:params:oauth:token-type:access_token",
            ["subject_token_type"] = subjectTokenType,
            ["subject_token"] = subjectToken
        };
        if (RefreshingGcsTokenProvider.GetOptionalString(
                credential,
                "workforce_pool_user_project") is { } userProject)
        {
            fields["options"] = JsonSerializer.Serialize(new
            {
                userProject
            });
        }

        var federated = await RequestFormTokenAsync(
            new Uri(tokenUrl, UriKind.Absolute),
            fields,
            cancellationToken).ConfigureAwait(false);
        return impersonationUrl is null
            ? federated
            : await ImpersonateAsync(
                new Uri(impersonationUrl, UriKind.Absolute),
                federated.Value,
                ReadImpersonationLifetime(credential),
                cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<string> ReadSubjectTokenAsync(
        JsonElement source,
        CancellationToken cancellationToken)
    {
        if (source.TryGetProperty("executable", out _) ||
            source.TryGetProperty("command", out _) ||
            source.TryGetProperty("environment_id", out _))
        {
            throw new PantsInvalidArgumentException(
                "GCS executable and AWS external-account credential sources are unsupported without SDK or process execution.");
        }

        string content;
        if (RefreshingGcsTokenProvider.GetOptionalString(source, "file") is { } file)
        {
            try
            {
                content = await File.ReadAllTextAsync(file, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                throw new PantsIOException(
                    "GCS external-account subject-token file could not be read.",
                    exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new PantsIOException(
                    "GCS external-account subject-token file could not be read.",
                    exception);
            }
        }
        else if (RefreshingGcsTokenProvider.GetOptionalString(source, "url") is { } url)
        {
            content = await ReadSubjectTokenUrlAsync(
                new Uri(url, UriKind.Absolute),
                source,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new PantsInvalidArgumentException(
                "GCS external-account ADC requires a supported file or URL credential_source.");
        }

        return ParseSubjectToken(source, content);
    }

    async ValueTask<string> ReadSubjectTokenUrlAsync(
        Uri uri,
        JsonElement source,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (source.TryGetProperty("headers", out var headers))
        {
            if (headers.ValueKind != JsonValueKind.Object)
            {
                throw new PantsInvalidArgumentException(
                    "GCS external-account credential_source.headers must be an object.");
            }

            foreach (var header in headers.EnumerateObject())
            {
                if (header.Value.ValueKind != JsonValueKind.String)
                {
                    throw new PantsInvalidArgumentException(
                        "GCS external-account credential-source header values must be strings.");
                }

                request.Headers.TryAddWithoutValidation(
                    header.Name,
                    header.Value.GetString());
            }
        }

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "GCS external-account subject-token request");
        return await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    async ValueTask<GcsAccessToken> RequestFormTokenAsync(
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
        EnsureSuccess(response, "GCS external-account token exchange");
        return await RefreshingGcsTokenProvider.ParseTokenResponseAsync(
            response,
            cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<GcsAccessToken> ImpersonateAsync(
        Uri uri,
        string sourceToken,
        ulong lifetimeSeconds,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    scope = ImpersonationScopes,
                    lifetime = $"{lifetimeSeconds}s"
                }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            sourceToken);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "GCS service-account impersonation request");
        return await ParseImpersonationResponseAsync(response, cancellationToken)
            .ConfigureAwait(false);
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
                "GCS external-account credential request exceeded its deadline.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new PantsIOException(
                "GCS external-account credential transport failed.",
                exception);
        }
    }

    static string ParseSubjectToken(JsonElement source, string content)
    {
        var format = source.TryGetProperty("format", out var formatElement)
            ? formatElement
            : default;
        var type = format.ValueKind == JsonValueKind.Object
            ? RefreshingGcsTokenProvider.GetOptionalString(format, "type")
            : null;
        if (type is null or "text")
        {
            var value = content.Trim();
            return value.Length == 0
                ? throw new PantsInvalidArgumentException(
                    "GCS external-account subject token is empty.")
                : value;
        }

        if (type != "json")
        {
            throw new PantsInvalidArgumentException(
                $"Unsupported GCS external-account subject-token format '{type}'.");
        }

        var field = RefreshingGcsTokenProvider.GetRequiredString(
            format,
            "subject_token_field_name");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException exception)
        {
            throw new PantsInvalidArgumentException(
                "GCS external-account subject-token JSON is malformed.",
                exception);
        }

        using (document)
        {
            return RefreshingGcsTokenProvider.GetRequiredString(
                document.RootElement,
                field);
        }
    }

    static ulong ReadImpersonationLifetime(JsonElement credential)
    {
        if (!credential.TryGetProperty(
                "service_account_impersonation",
                out var impersonation) ||
            impersonation.ValueKind != JsonValueKind.Object ||
            !impersonation.TryGetProperty("token_lifetime_seconds", out var lifetime))
        {
            return 3600;
        }

        if (lifetime.ValueKind != JsonValueKind.Number ||
            !lifetime.TryGetUInt64(out var seconds) ||
            seconds is < 600 or > 43_200)
        {
            throw new PantsInvalidArgumentException(
                "GCS external-account impersonation token_lifetime_seconds must be an integer from 600 through 43200.");
        }

        return seconds;
    }

    static async ValueTask<GcsAccessToken> ParseImpersonationResponseAsync(
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
            throw new PantsIOException(
                "GCS service-account impersonation JSON is malformed.",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            var token = RefreshingGcsTokenProvider.GetOptionalString(root, "accessToken");
            var expiry = RefreshingGcsTokenProvider.GetOptionalString(root, "expireTime");
            if (token is null ||
                expiry is null ||
                !DateTimeOffset.TryParse(
                    expiry,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var expiresAt) ||
                expiresAt <= DateTimeOffset.UtcNow)
            {
                throw new PantsIOException(
                    "GCS service-account impersonation response must contain a non-empty token and future RFC3339 expiry.");
            }

            return new GcsAccessToken(token, expiresAt);
        }
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
