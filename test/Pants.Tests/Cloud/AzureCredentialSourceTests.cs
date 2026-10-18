using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

[Collection(CredentialEnvironmentDefinition.Name)]
public sealed class AzureCredentialSourceTests
{
    static readonly string[] AzureEnvironmentVariables =
    [
        "AZURE_STORAGE_CONNECTION_STRING",
        "AZURE_STORAGE_ACCOUNT",
        "AZURE_STORAGE_KEY",
        "AZURE_STORAGE_ACCOUNT_KEY",
        "AZURE_STORAGE_SAS_TOKEN",
        "AZURE_STORAGE_BEARER_TOKEN",
        "AZURE_TENANT_ID",
        "AZURE_CLIENT_ID",
        "AZURE_CLIENT_SECRET",
        "AZURE_FEDERATED_TOKEN_FILE",
        "AZURE_AUTHORITY_HOST",
        "IDENTITY_ENDPOINT",
        "IDENTITY_HEADER",
        "MSI_ENDPOINT",
        "MSI_SECRET"
    ];

    [Fact]
    public async Task ShouldResolveConnectionStringAccountKeyAndEndpointSuffix()
    {
        const string key = "c2VjcmV0LWtleQ==";
        using var handler = new CredentialHttpHandler(static (_, _) => AzureObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            string.Empty,
            null,
            new PantsAzureCredentialSource.ConnectionString(
                $"DefaultEndpointsProtocol=https;AccountName=connection-account;AccountKey={key};EndpointSuffix=example.test"),
            client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("connection-account.blob.example.test", request.Uri.Host);
        Assert.StartsWith(
            "SharedKey connection-account:",
            request.Header("Authorization"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(key, request.Header("Authorization"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldResolveAzureStorageEnvironmentKey()
    {
        using var environment = SetAzureEnvironment(new Dictionary<string, string?>
        {
            ["AZURE_STORAGE_KEY"] = "c2VjcmV0LWtleQ=="
        });
        using var handler = new CredentialHttpHandler(static (_, _) => AzureObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            "account",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.StorageEnvironment(),
            client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        Assert.StartsWith(
            "SharedKey account:",
            Assert.Single(handler.Requests).Header("Authorization"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldRefreshAzureEnvironmentClientSecretCredential()
    {
        using var environment = SetAzureEnvironment(new Dictionary<string, string?>
        {
            ["AZURE_TENANT_ID"] = "tenant",
            ["AZURE_CLIENT_ID"] = "client",
            ["AZURE_CLIENT_SECRET"] = "client-secret",
            ["AZURE_AUTHORITY_HOST"] = "https://authority.example.test"
        });
        var issued = 0;
        using var handler = new CredentialHttpHandler((request, _) =>
        {
            if (request.Uri.Host == "authority.example.test")
            {
                issued++;
                Assert.Equal("/tenant/oauth2/v2.0/token", request.Uri.AbsolutePath);
                Assert.Contains("grant_type=client_credentials", request.Body, StringComparison.Ordinal);
                Assert.Contains("client_secret=client-secret", request.Body, StringComparison.Ordinal);
                return AzureTokenResponse($"oauth-token-{issued}", 60);
            }

            return AzureObjectResponse();
        });
        using var client = new HttpClient(handler);
        var store = CreateStore(
            "account",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.EnvironmentClientSecret(),
            client);

        Assert.NotNull(await store.GetAsync("first", CancellationToken.None));
        Assert.NotNull(await store.GetAsync("second", CancellationToken.None));

        Assert.Equal(2, issued);
        var objects = handler.Requests.Where(static request => request.Uri.Host == "storage.example.test").ToArray();
        Assert.Equal("Bearer oauth-token-1", objects[0].Header("Authorization"));
        Assert.Equal("Bearer oauth-token-2", objects[1].Header("Authorization"));
    }

    [Fact]
    public async Task ShouldNotLeakAzureTokenEndpointResponseBodyGivenFailure()
    {
        using var environment = SetAzureEnvironment(new Dictionary<string, string?>
        {
            ["AZURE_TENANT_ID"] = "tenant",
            ["AZURE_CLIENT_ID"] = "client",
            ["AZURE_CLIENT_SECRET"] = "never-leak-client-secret",
            ["AZURE_AUTHORITY_HOST"] = "https://authority.example.test"
        });
        const string responseSecret = "never-leak-provider-response";
        using var handler = new CredentialHttpHandler(static (_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(responseSecret)
            });
        using var client = new HttpClient(handler);
        var store = CreateStore(
            "account",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.EnvironmentClientSecret(),
            client);

        var exception =
            await Assert.ThrowsAsync<PantsIOException>(() => store.GetAsync("object", CancellationToken.None).AsTask());

        Assert.DoesNotContain(responseSecret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("never-leak-client-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldClassifyNonStringAzureAccessTokenAsIoFailure()
    {
        using var environment = SetAzureEnvironment(new Dictionary<string, string?>
        {
            ["AZURE_TENANT_ID"] = "tenant",
            ["AZURE_CLIENT_ID"] = "client",
            ["AZURE_CLIENT_SECRET"] = "client-secret",
            ["AZURE_AUTHORITY_HOST"] = "https://authority.example.test"
        });
        using var handler = new CredentialHttpHandler(static (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"access_token\":42,\"expires_in\":3600}")
        });
        using var client = new HttpClient(handler);
        var store = CreateStore(
            "account",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.EnvironmentClientSecret(),
            client);

        await Assert.ThrowsAsync<PantsIOException>(() => store.GetAsync("object", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ShouldExchangeAzureWorkloadIdentityAssertion()
    {
        using var directory = new TemporaryDirectory();
        var tokenPath = Path.Combine(directory.Path, "federated-token");
        await File.WriteAllTextAsync(tokenPath, "federated-assertion");
        using var environment = SetAzureEnvironment(new Dictionary<string, string?>
        {
            ["AZURE_AUTHORITY_HOST"] = "https://authority.example.test"
        });
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "authority.example.test"
                ? AzureTokenResponse("workload-token", 3600)
                : AzureObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            "account",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.WorkloadIdentity(
                "tenant",
                "client",
                tokenPath),
            client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        var tokenRequest = handler.Requests[0];
        Assert.Contains("client_assertion=federated-assertion", tokenRequest.Body, StringComparison.Ordinal);
        Assert.Contains(
            "client_assertion_type=urn%3Aietf%3Aparams%3Aoauth%3Aclient-assertion-type%3Ajwt-bearer",
            tokenRequest.Body,
            StringComparison.Ordinal);
        Assert.Equal("Bearer workload-token", handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task ShouldResolveManagedIdentityWithEndpointSpecificHeader()
    {
        using var environment = SetAzureEnvironment(new Dictionary<string, string?>
        {
            ["IDENTITY_ENDPOINT"] = "http://127.0.0.1/managed-identity",
            ["IDENTITY_HEADER"] = "rotating-secret",
            ["AZURE_CLIENT_ID"] = "user-assigned-client"
        });
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "127.0.0.1"
                ? ManagedIdentityTokenResponse("managed-token")
                : AzureObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            "account",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.ManagedIdentity(),
            client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        var tokenRequest = handler.Requests[0];
        Assert.Equal("rotating-secret", tokenRequest.Header("X-IDENTITY-HEADER"));
        Assert.Contains("client_id=user-assigned-client", tokenRequest.Uri.Query, StringComparison.Ordinal);
        Assert.Equal("Bearer managed-token", handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task ShouldPreferCompleteClientSecretInAzureLightweightDefaultChain()
    {
        using var directory = new TemporaryDirectory();
        var workloadPath = Path.Combine(directory.Path, "workload-token");
        await File.WriteAllTextAsync(workloadPath, "workload-assertion");
        using var environment = SetAzureEnvironment(new Dictionary<string, string?>
        {
            ["AZURE_TENANT_ID"] = "tenant",
            ["AZURE_CLIENT_ID"] = "client",
            ["AZURE_CLIENT_SECRET"] = "preferred-secret",
            ["AZURE_FEDERATED_TOKEN_FILE"] = workloadPath,
            ["AZURE_AUTHORITY_HOST"] = "https://authority.example.test",
            ["IDENTITY_ENDPOINT"] = "http://127.0.0.1/managed-identity",
            ["IDENTITY_HEADER"] = "identity-secret"
        });
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "authority.example.test"
                ? AzureTokenResponse("default-chain-token", 3600)
                : AzureObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            "account",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.LightweightDefaultChain(),
            client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        Assert.Contains("client_secret=preferred-secret", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("client_assertion", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRejectInsecureBlobEndpointForAzureIdentityCredential()
    {
        using var client = new HttpClient(new CredentialHttpHandler(static (_, _) => AzureObjectResponse()));

        var exception = Assert.Throws<PantsInvalidArgumentException>(() => CreateStore(
            "account",
            new Uri("http://storage.example.test"),
            new PantsAzureCredentialSource.ManagedIdentity(),
            client));

        Assert.Contains("HTTPS origin", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://account.blob.core.usgovcloudapi.net", "login.microsoftonline.us")]
    [InlineData("https://account.blob.core.chinacloudapi.cn", "login.chinacloudapi.cn")]
    [InlineData("https://account.blob.core.windows.net", "login.microsoftonline.com")]
    public async Task ShouldDeriveAzureAuthorityFromBlobEndpointWhenAuthorityHostUnset(
        string blobEndpoint,
        string authorityHost)
    {
        using var environment = SetAzureEnvironment(new Dictionary<string, string?>
        {
            ["AZURE_TENANT_ID"] = "tenant",
            ["AZURE_CLIENT_ID"] = "client",
            ["AZURE_CLIENT_SECRET"] = "client-secret"
        });
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == authorityHost
                ? AzureTokenResponse("sovereign-token", 3600)
                : AzureObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            "account",
            new Uri(blobEndpoint),
            new PantsAzureCredentialSource.EnvironmentClientSecret(),
            client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        var tokenRequest = handler.Requests[0];
        Assert.Equal(authorityHost, tokenRequest.Uri.Host);
        Assert.Equal("/tenant/oauth2/v2.0/token", tokenRequest.Uri.AbsolutePath);
        Assert.Equal("Bearer sovereign-token", handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task ShouldPreferExplicitAzureAuthorityHostOverSovereignBlobEndpoint()
    {
        using var environment = SetAzureEnvironment(new Dictionary<string, string?>
        {
            ["AZURE_TENANT_ID"] = "tenant",
            ["AZURE_CLIENT_ID"] = "client",
            ["AZURE_CLIENT_SECRET"] = "client-secret",
            ["AZURE_AUTHORITY_HOST"] = "https://authority.example.test"
        });
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "authority.example.test"
                ? AzureTokenResponse("explicit-authority-token", 3600)
                : AzureObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            "account",
            new Uri("https://account.blob.core.usgovcloudapi.net"),
            new PantsAzureCredentialSource.EnvironmentClientSecret(),
            client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        Assert.Equal("authority.example.test", handler.Requests[0].Uri.Host);
        Assert.DoesNotContain(handler.Requests, static request => request.Uri.Host == "login.microsoftonline.us");
    }

    [Theory]
    [InlineData("http://authority.example.test")]
    [InlineData("https://user:synthetic-password@authority.example.test")]
    [InlineData("https://authority.example.test/tenant")]
    [InlineData("https://authority.example.test?synthetic=1")]
    [InlineData("https://authority.example.test#synthetic")]
    [InlineData(" https://authority.example.test")]
    [InlineData("https://authority.example.test ")]
    public async Task ShouldRejectMalformedAzureAuthorityHostWithoutSendingRequest(string authorityHost)
    {
        using var environment = SetAzureEnvironment(new Dictionary<string, string?>
        {
            ["AZURE_TENANT_ID"] = "tenant",
            ["AZURE_CLIENT_ID"] = "client",
            ["AZURE_CLIENT_SECRET"] = "client-secret",
            ["AZURE_AUTHORITY_HOST"] = authorityHost
        });
        using var handler = new CredentialHttpHandler(static (_, _) => AzureObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            "account",
            new Uri("https://account.blob.core.usgovcloudapi.net"),
            new PantsAzureCredentialSource.EnvironmentClientSecret(),
            client);

        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            store.GetAsync("object", CancellationToken.None).AsTask());

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ShouldRejectWhitespaceOnlyAzureFederatedTokenBeforeSendingRequest()
    {
        using var directory = new TemporaryDirectory();
        var tokenPath = Path.Combine(directory.Path, "federated-token");
        await File.WriteAllTextAsync(tokenPath, " \r\n\t ");
        using var environment = SetAzureEnvironment(new Dictionary<string, string?>
        {
            ["AZURE_AUTHORITY_HOST"] = "https://authority.example.test"
        });
        using var handler = new CredentialHttpHandler(static (_, _) => AzureObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            "account",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.WorkloadIdentity("tenant", "client", tokenPath),
            client);

        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            store.GetAsync("object", CancellationToken.None).AsTask());

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("{\"access_token\":\"managed-token\"}")]
    [InlineData("{\"access_token\":\"managed-token\",\"expires_on\":\"soon\"}")]
    [InlineData("{\"access_token\":\"managed-token\",\"expires_on\":\"946684800\"}")]
    [InlineData("{\"access_token\":\"managed-token\",\"expires_in\":3600}")]
    public async Task ShouldRejectManagedIdentityTokenWithoutFutureAbsoluteExpiry(string responseBody)
    {
        using var environment = SetAzureEnvironment(new Dictionary<string, string?>
        {
            ["IDENTITY_ENDPOINT"] = "http://127.0.0.1/managed-identity",
            ["IDENTITY_HEADER"] = "rotating-secret"
        });
        using var handler = new CredentialHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody)
        });
        using var client = new HttpClient(handler);
        var store = CreateStore(
            "account",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.ManagedIdentity(),
            client,
            new FixedTimeProvider(new DateTimeOffset(2050, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        await Assert.ThrowsAsync<PantsIOException>(() =>
            store.GetAsync("object", CancellationToken.None).AsTask());

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ShouldSendLegacySecretHeaderAndApiVersionGivenMsiEndpoint()
    {
        using var environment = SetAzureEnvironment(new Dictionary<string, string?>
        {
            ["MSI_ENDPOINT"] = "http://127.0.0.1/msi",
            ["MSI_SECRET"] = "legacy-msi-secret"
        });
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "127.0.0.1"
                ? ManagedIdentityTokenResponse("legacy-msi-token")
                : AzureObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            "account",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.ManagedIdentity(),
            client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        var tokenRequest = handler.Requests[0];
        Assert.Equal("legacy-msi-secret", tokenRequest.Header("Secret"));
        Assert.Null(tokenRequest.Header("X-IDENTITY-HEADER"));
        Assert.Contains("api-version=2017-09-01", tokenRequest.Uri.Query, StringComparison.Ordinal);
        Assert.Equal("Bearer legacy-msi-token", handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task ShouldSendMetadataHeaderAndApiVersionGivenVmImdsEndpoint()
    {
        using var environment = SetAzureEnvironment(new Dictionary<string, string?>());
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "169.254.169.254"
                ? ManagedIdentityTokenResponse("imds-token")
                : AzureObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            "account",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.ManagedIdentity(),
            client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        var tokenRequest = handler.Requests[0];
        Assert.Equal("true", tokenRequest.Header("Metadata"));
        Assert.Contains("api-version=2019-08-01", tokenRequest.Uri.Query, StringComparison.Ordinal);
        Assert.Equal("Bearer imds-token", handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task ShouldSelectManagedIdentityGivenWhitespaceOnlyAzureDefaultChainVariables()
    {
        using var environment = SetAzureEnvironment(new Dictionary<string, string?>
        {
            ["AZURE_TENANT_ID"] = "   ",
            ["AZURE_CLIENT_ID"] = " ",
            ["AZURE_CLIENT_SECRET"] = "\t",
            ["AZURE_FEDERATED_TOKEN_FILE"] = "   ",
            ["IDENTITY_ENDPOINT"] = "http://127.0.0.1/managed-identity",
            ["IDENTITY_HEADER"] = "identity-secret"
        });
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "127.0.0.1"
                ? ManagedIdentityTokenResponse("selected-managed-token")
                : AzureObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            "account",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.LightweightDefaultChain(),
            client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        var tokenRequest = handler.Requests[0];
        Assert.Equal("127.0.0.1", tokenRequest.Uri.Host);
        Assert.Equal("identity-secret", tokenRequest.Header("X-IDENTITY-HEADER"));
        Assert.DoesNotContain("client_id=", tokenRequest.Uri.Query, StringComparison.Ordinal);
        Assert.Equal("Bearer selected-managed-token", handler.Requests[1].Header("Authorization"));
    }

    [Theory]
    [InlineData("AZURE_TENANT_ID")]
    [InlineData("AZURE_CLIENT_ID")]
    public async Task ShouldSelectManagedIdentityGivenBlankTenantOrClientInDefaultChain(string blankName)
    {
        using var directory = new TemporaryDirectory();
        var workloadPath = Path.Combine(directory.Path, "workload-token");
        await File.WriteAllTextAsync(workloadPath, "workload-assertion");
        using var environment = SetAzureEnvironment(new Dictionary<string, string?>
        {
            ["AZURE_TENANT_ID"] = "tenant",
            ["AZURE_CLIENT_ID"] = "client",
            ["AZURE_CLIENT_SECRET"] = "client-secret",
            ["AZURE_FEDERATED_TOKEN_FILE"] = workloadPath,
            ["AZURE_AUTHORITY_HOST"] = "https://authority.example.test",
            ["IDENTITY_ENDPOINT"] = "http://127.0.0.1/managed-identity",
            ["IDENTITY_HEADER"] = "identity-secret",
            [blankName] = "   "
        });
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "127.0.0.1"
                ? ManagedIdentityTokenResponse("blank-fallback-token")
                : AzureObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            "account",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.LightweightDefaultChain(),
            client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        Assert.Equal("127.0.0.1", handler.Requests[0].Uri.Host);
        Assert.DoesNotContain(handler.Requests, static request => request.Uri.Host == "authority.example.test");
    }

    [Fact]
    public async Task ShouldFallBackToWorkloadIdentityGivenBlankClientSecretInDefaultChain()
    {
        using var directory = new TemporaryDirectory();
        var workloadPath = Path.Combine(directory.Path, "workload-token");
        await File.WriteAllTextAsync(workloadPath, "workload-assertion");
        using var environment = SetAzureEnvironment(new Dictionary<string, string?>
        {
            ["AZURE_TENANT_ID"] = "tenant",
            ["AZURE_CLIENT_ID"] = "client",
            ["AZURE_CLIENT_SECRET"] = "   ",
            ["AZURE_FEDERATED_TOKEN_FILE"] = workloadPath,
            ["AZURE_AUTHORITY_HOST"] = "https://authority.example.test"
        });
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "authority.example.test"
                ? AzureTokenResponse("workload-fallback-token", 3600)
                : AzureObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            "account",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.LightweightDefaultChain(),
            client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        Assert.Contains("client_assertion=workload-assertion", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRedactAzureCredentialSecretsFromFormatting()
    {
        const string secret = "render-azure-secret";
        object[] sources =
        [
            new PantsAzureCredentialSource.SharedKey(secret),
            new PantsAzureCredentialSource.SasToken(secret),
            new PantsAzureCredentialSource.ConnectionString($"AccountKey={secret}"),
            AzureResolvedCredential.SharedKey(secret),
            AzureResolvedCredential.Sas(secret)
        ];

        Assert.All(sources, source =>
            Assert.DoesNotContain(secret, source.ToString(), StringComparison.Ordinal));
    }

    static AzureBlobObjectStore CreateStore(
        string account,
        Uri? endpoint,
        PantsAzureCredentialSource source,
        HttpClient client,
        TimeProvider? timeProvider = null) => new(
        new PantsAzureBlobProvider(
            account,
            "container",
            endpoint,
            source),
        string.Empty,
        client,
        TimeSpan.FromSeconds(5),
        null,
        timeProvider);

    static EnvironmentVariableScope SetAzureEnvironment(
        IReadOnlyDictionary<string, string?> overrides)
    {
        var values = AzureEnvironmentVariables.ToDictionary(
            static name => name,
            static _ => (string?)null,
            StringComparer.Ordinal);
        foreach (var pair in overrides)
        {
            values[pair.Key] = pair.Value;
        }

        return new EnvironmentVariableScope(values);
    }

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    static HttpResponseMessage AzureObjectResponse() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent("value"u8.ToArray()),
        Headers = { ETag = new EntityTagHeaderValue("\"etag\"") }
    };

    static HttpResponseMessage AzureTokenResponse(string token, int expiresIn) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""{"access_token":"{{token}}","expires_in":{{expiresIn}}}""")
    };

    static HttpResponseMessage ManagedIdentityTokenResponse(string token) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""{"access_token":"{{token}}","expires_on":"{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}}"}""")
    };
}
