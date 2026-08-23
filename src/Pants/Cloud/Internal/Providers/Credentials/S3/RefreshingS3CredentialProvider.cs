using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Cntryl.Pants;

internal sealed class RefreshingS3CredentialProvider : IS3CredentialProvider, IDisposable
{
    readonly string _region;
    readonly HttpClient _httpClient;
    readonly TimeSpan _timeout;
    readonly SemaphoreSlim _gate = new(1, 1);
    CachedS3Credentials? _cached;
    int _disposed;

    public RefreshingS3CredentialProvider(
        string region,
        HttpClient httpClient,
        TimeSpan timeout)
    {
        _region = region;
        _httpClient = httpClient;
        _timeout = timeout;
    }

    public async ValueTask<S3Credentials> GetCredentialsAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_cached is { } cached && !cached.RequiresRefresh(DateTimeOffset.UtcNow))
        {
            return cached.Credentials;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is { } current && !current.RequiresRefresh(DateTimeOffset.UtcNow))
            {
                return current.Credentials;
            }

            _cached = await ResolveFreshAsync(cancellationToken).ConfigureAwait(false);
            return _cached.Credentials;
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

    async ValueTask<CachedS3Credentials> ResolveFreshAsync(
        CancellationToken cancellationToken)
    {
        if (S3CredentialResolver.FromEnvironment() is { } environment)
        {
            return new CachedS3Credentials(environment, ExpiresAt: null);
        }

        if (S3CredentialResolver.FromDefaultProfile() is { } profile)
        {
            return new CachedS3Credentials(profile, ExpiresAt: null);
        }

        if (Environment.GetEnvironmentVariable("AWS_ROLE_ARN") is not null ||
            Environment.GetEnvironmentVariable("AWS_WEB_IDENTITY_TOKEN_FILE") is not null)
        {
            return await ResolveWebIdentityAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Environment.GetEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI") is not null ||
            Environment.GetEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_FULL_URI") is not null)
        {
            return await ResolveContainerAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Environment.GetEnvironmentVariable("AWS_EC2_METADATA_DISABLED")
            ?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new PantsInvalidArgumentException(
                "EC2 IMDS credential resolution is disabled by AWS_EC2_METADATA_DISABLED.");
        }

        return await ResolveImdsAsync(cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<CachedS3Credentials> ResolveWebIdentityAsync(
        CancellationToken cancellationToken)
    {
        var roleArn = RequireEnvironment("AWS_ROLE_ARN");
        var tokenPath = RequireEnvironment("AWS_WEB_IDENTITY_TOKEN_FILE");
        string token;
        try
        {
            token = (await File.ReadAllTextAsync(tokenPath, cancellationToken)
                .ConfigureAwait(false)).Trim();
        }
        catch (IOException exception)
        {
            throw new PantsIOException("AWS web-identity token file could not be read.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new PantsIOException("AWS web-identity token file could not be read.", exception);
        }

        if (token.Length == 0)
        {
            throw new PantsInvalidArgumentException("AWS web-identity token file is empty.");
        }

        var sessionName = Environment.GetEnvironmentVariable("AWS_ROLE_SESSION_NAME");
        if (string.IsNullOrWhiteSpace(sessionName))
        {
            sessionName = $"pants-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        }

        var dnsSuffix = _region.StartsWith("cn-", StringComparison.Ordinal)
            ? "amazonaws.com.cn"
            : "amazonaws.com";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://sts.{_region}.{dnsSuffix}/")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Action"] = "AssumeRoleWithWebIdentity",
                ["Version"] = "2011-06-15",
                ["RoleArn"] = roleArn,
                ["RoleSessionName"] = sessionName,
                ["WebIdentityToken"] = token
            })
        };
        using var response = await SendCredentialRequestAsync(request, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, "AWS STS web-identity credential request");
        var body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        return ParseTemporaryXml(body, "AWS STS web identity");
    }

    async ValueTask<CachedS3Credentials> ResolveContainerAsync(
        CancellationToken cancellationToken)
    {
        var endpoint = ResolveContainerEndpoint();
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        var tokenFile = Environment.GetEnvironmentVariable(
            "AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE");
        if (!string.IsNullOrWhiteSpace(tokenFile))
        {
            string token;
            try
            {
                token = (await File.ReadAllTextAsync(tokenFile, cancellationToken)
                    .ConfigureAwait(false)).Trim();
            }
            catch (IOException exception)
            {
                throw new PantsIOException(
                    "AWS container authorization token file could not be read.",
                    exception);
            }

            if (token.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("Authorization", token);
            }
        }
        else if (Environment.GetEnvironmentVariable("AWS_CONTAINER_AUTHORIZATION_TOKEN") is { } authorization &&
            !string.IsNullOrWhiteSpace(authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await SendCredentialRequestAsync(request, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, "AWS container credential request");
        return await ParseTemporaryJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<CachedS3Credentials> ResolveImdsAsync(
        CancellationToken cancellationToken)
    {
        var endpoint = ResolveImdsEndpoint();
        using var tokenRequest = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri(endpoint, "/latest/api/token"));
        tokenRequest.Headers.TryAddWithoutValidation(
            "X-aws-ec2-metadata-token-ttl-seconds",
            "21600");
        using var tokenResponse = await SendCredentialRequestAsync(
            tokenRequest,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(tokenResponse, "AWS IMDSv2 token request");
        var token = (await tokenResponse.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false)).Trim();
        if (token.Length == 0)
        {
            throw new PantsIOException("AWS IMDSv2 token response was empty.");
        }

        using var roleRequest = CreateImdsRequest(
            new Uri(endpoint, "/latest/meta-data/iam/security-credentials/"),
            token);
        using var roleResponse = await SendCredentialRequestAsync(
            roleRequest,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(roleResponse, "AWS IMDS role-name request");
        var role = (await roleResponse.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false)).Split('\n')[0].Trim();
        if (role.Length == 0)
        {
            throw new PantsIOException("AWS IMDS returned an empty IAM role name.");
        }

        using var credentialRequest = CreateImdsRequest(
            new Uri(
                endpoint,
                "/latest/meta-data/iam/security-credentials/" + Uri.EscapeDataString(role)),
            token);
        using var credentialResponse = await SendCredentialRequestAsync(
            credentialRequest,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(credentialResponse, "AWS IMDS credential request");
        return await ParseTemporaryJsonAsync(credentialResponse, cancellationToken)
            .ConfigureAwait(false);
    }

    async ValueTask<HttpResponseMessage> SendCredentialRequestAsync(
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
                "AWS credential resolution exceeded its deadline.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new PantsIOException("AWS credential transport failed.", exception);
        }
    }

    static Uri ResolveContainerEndpoint()
    {
        var relative = Environment.GetEnvironmentVariable(
            "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI");
        if (relative is not null)
        {
            if (!relative.StartsWith('/') ||
                relative.StartsWith("//", StringComparison.Ordinal) ||
                relative.Contains('\\'))
            {
                throw new PantsInvalidArgumentException(
                    "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI must be an absolute safe path.");
            }

            return new Uri("http://169.254.170.2" + relative, UriKind.Absolute);
        }

        var full = RequireEnvironment("AWS_CONTAINER_CREDENTIALS_FULL_URI");
        if (!Uri.TryCreate(full, UriKind.Absolute, out var endpoint) ||
            endpoint.UserInfo.Length > 0 ||
            endpoint.Fragment.Length > 0 ||
            endpoint.Scheme is not ("http" or "https"))
        {
            throw new PantsInvalidArgumentException(
                "AWS_CONTAINER_CREDENTIALS_FULL_URI must be an absolute HTTP(S) URL without credentials or a fragment.");
        }

        if (endpoint.Scheme == Uri.UriSchemeHttp && !IsAllowedHttpCredentialHost(endpoint.Host))
        {
            throw new PantsInvalidArgumentException(
                "Insecure AWS_CONTAINER_CREDENTIALS_FULL_URI host is not an allowed local credential endpoint.");
        }

        return endpoint;
    }

    static Uri ResolveImdsEndpoint()
    {
        var explicitEndpoint = Environment.GetEnvironmentVariable(
            "AWS_EC2_METADATA_SERVICE_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(explicitEndpoint))
        {
            if (!Uri.TryCreate(explicitEndpoint.TrimEnd('/'), UriKind.Absolute, out var parsed) ||
                parsed.Scheme is not ("http" or "https") ||
                string.IsNullOrWhiteSpace(parsed.Host))
            {
                throw new PantsInvalidArgumentException(
                    "AWS EC2 metadata endpoint must be an absolute HTTP(S) URL.");
            }

            return parsed;
        }

        var mode = Environment.GetEnvironmentVariable(
            "AWS_EC2_METADATA_SERVICE_ENDPOINT_MODE");
        return mode switch
        {
            null or "IPv4" => new Uri("http://169.254.169.254"),
            var value when value.Equals("IPv6", StringComparison.OrdinalIgnoreCase) =>
                new Uri("http://[fd00:ec2::254]"),
            _ => throw new PantsInvalidArgumentException(
                $"Invalid AWS_EC2_METADATA_SERVICE_ENDPOINT_MODE '{mode}'.")
        };
    }

    static HttpRequestMessage CreateImdsRequest(Uri uri, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token", token);
        return request;
    }

    static async ValueTask<CachedS3Credentials> ParseTemporaryJsonAsync(
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
            throw new PantsIOException("AWS temporary credential JSON is malformed.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new PantsIOException(
                    "AWS temporary credential response must be a JSON object.");
            }

            var accessKey = RequiredJsonString(root, "AccessKeyId");
            var secretKey = RequiredJsonString(root, "SecretAccessKey");
            var sessionToken = root.TryGetProperty("Token", out var token)
                ? RequiredJsonString(token, "Token")
                : RequiredJsonString(root, "SessionToken");
            var expiration = ParseExpiration(RequiredJsonString(root, "Expiration"));
            return new CachedS3Credentials(
                new S3Credentials(accessKey, secretKey, sessionToken),
                expiration);
        }
    }

    static CachedS3Credentials ParseTemporaryXml(string body, string source)
    {
        var accessKey = RequiredXmlValue(body, "AccessKeyId", source);
        var secretKey = RequiredXmlValue(body, "SecretAccessKey", source);
        var sessionToken = RequiredXmlValue(body, "SessionToken", source);
        var expiration = ParseExpiration(RequiredXmlValue(body, "Expiration", source));
        return new CachedS3Credentials(
            new S3Credentials(accessKey, secretKey, sessionToken),
            expiration);
    }

    static string RequiredXmlValue(string body, string name, string source)
    {
        var value = CloudProviderXml.TryReadElementValue(body, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new PantsIOException($"{source} response omitted {name}.")
            : value;
    }

    static string RequiredJsonString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var direct = element.GetString();
            return string.IsNullOrWhiteSpace(direct)
                ? throw new PantsIOException(
                    $"AWS temporary credential field {name} was empty.")
                : direct;
        }

        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } text &&
            !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        throw new PantsIOException($"AWS temporary credential response omitted {name}.");
    }

    static DateTimeOffset ParseExpiration(string value)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var expiration) ||
            expiration <= DateTimeOffset.UtcNow)
        {
            throw new PantsIOException(
                "AWS temporary credential expiration is missing, invalid, or not in the future.");
        }

        return expiration;
    }

    static bool IsAllowedHttpCredentialHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            return false;
        }

        return IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Parse("169.254.170.2")) ||
            address.Equals(IPAddress.Parse("169.254.170.23")) ||
            address.Equals(IPAddress.Parse("fd00:ec2::23"));
    }

    static string RequireEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new PantsInvalidArgumentException($"Missing or empty {name}.")
            : value;
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
