using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

[Collection(CredentialEnvironmentDefinition.Name)]
public sealed class GcsCredentialSourceTests
{
    static readonly string[] GcsEnvironmentVariables =
    [
        "GOOGLE_APPLICATION_CREDENTIALS",
        "GOOGLE_OAUTH_ACCESS_TOKEN",
        "GCE_METADATA_HOST"
    ];

    [Fact]
    public async Task ShouldResolveApplicationDefaultCredentialFile()
    {
        using var directory = new TemporaryDirectory();
        var credentialPath = Path.Combine(directory.Path, "adc.json");
        await WriteAuthorizedUserAsync(credentialPath, "adc-secret", "adc-refresh");
        using var environment = SetGcsEnvironment(new Dictionary<string, string?>
        {
            ["GOOGLE_APPLICATION_CREDENTIALS"] = credentialPath
        });
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "oauth.example.test"
                ? GcsTokenResponse("adc-token", 3600)
                : GcsObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            new PantsGcsCredentialSource.ApplicationDefault(),
            client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        Assert.Contains("refresh_token=adc-refresh", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Equal("Bearer adc-token", handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task ShouldSignAndExchangeGcsServiceAccountJwt()
    {
        using var directory = new TemporaryDirectory();
        using var rsa = RSA.Create(2048);
        var credentialPath = Path.Combine(directory.Path, "service-account.json");
        await File.WriteAllTextAsync(
            credentialPath,
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["type"] = "service_account",
                ["client_email"] = "service@example.test",
                ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
                ["token_uri"] = "https://oauth.example.test/token"
            }));
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "oauth.example.test"
                ? GcsTokenResponse("service-token", 3600)
                : GcsObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            new PantsGcsCredentialSource.ServiceAccountJsonFile(credentialPath),
            client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        var tokenRequest = handler.Requests[0];
        Assert.Contains(
            "grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer",
            tokenRequest.Body,
            StringComparison.Ordinal);
        Assert.Contains("assertion=", tokenRequest.Body, StringComparison.Ordinal);
        Assert.Equal("Bearer service-token", handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task ShouldRefreshAuthorizedUserTokenBeforeExpiry()
    {
        using var directory = new TemporaryDirectory();
        var credentialPath = Path.Combine(directory.Path, "authorized-user.json");
        await WriteAuthorizedUserAsync(credentialPath, "client-secret", "refresh-secret");
        var issued = 0;
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "oauth.example.test"
                ? GcsTokenResponse($"refreshed-token-{++issued}", 60)
                : GcsObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            new PantsGcsCredentialSource.AuthorizedUserJsonFile(credentialPath),
            client);

        Assert.NotNull(await store.GetAsync("first", CancellationToken.None));
        Assert.NotNull(await store.GetAsync("second", CancellationToken.None));

        Assert.Equal(2, issued);
        var objects = handler.Requests.Where(static request => request.Uri.Host == "gcs.example.test").ToArray();
        Assert.Equal("Bearer refreshed-token-1", objects[0].Header("Authorization"));
        Assert.Equal("Bearer refreshed-token-2", objects[1].Header("Authorization"));
    }

    [Fact]
    public async Task ShouldResolveGcsMetadataServerWithRequiredFlavorHeader()
    {
        using var environment = SetGcsEnvironment(new Dictionary<string, string?>
        {
            ["GCE_METADATA_HOST"] = "metadata.example.test"
        });
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "metadata.example.test"
                ? GcsTokenResponse("metadata-token", 3600)
                : GcsObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(new PantsGcsCredentialSource.MetadataServer(), client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        Assert.Equal("Google", handler.Requests[0].Header("Metadata-Flavor"));
        Assert.Equal("Bearer metadata-token", handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task ShouldRouteMetadataCredentialsThroughIndependentClient()
    {
        using var environment = SetGcsEnvironment(new Dictionary<string, string?>
        {
            ["GCE_METADATA_HOST"] = "metadata.example.test"
        });
        using var storageHandler = new CredentialHttpHandler(static (_, _) => GcsObjectResponse());
        using var credentialHandler = new CredentialHttpHandler(static (_, _) =>
            GcsTokenResponse("metadata-token", 3600));
        using var storageClient = new HttpClient(storageHandler);
        using var credentialClient = new HttpClient(credentialHandler);
        var store = new GcsObjectStore(
            new PantsGcsProvider(
                "bucket",
                "project",
                new Uri("https://gcs.example.test"),
                PantsGcsApiStyle.Json,
                new PantsGcsCredentialSource.MetadataServer()),
            string.Empty,
            storageClient,
            TimeSpan.FromSeconds(5),
            credentialClient);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        Assert.Single(credentialHandler.Requests);
        Assert.Equal("metadata.example.test", credentialHandler.Requests[0].Uri.Host);
        Assert.Single(storageHandler.Requests);
        Assert.Equal("gcs.example.test", storageHandler.Requests[0].Uri.Host);
    }

    [Fact]
    public async Task ShouldExchangeFileSourcedExternalAccountCredentialFromAdc()
    {
        using var directory = new TemporaryDirectory();
        var subjectPath = Path.Combine(directory.Path, "subject-token");
        var credentialPath = Path.Combine(directory.Path, "external-account.json");
        await File.WriteAllTextAsync(subjectPath, "external-subject");
        await File.WriteAllTextAsync(
            credentialPath,
            JsonSerializer.Serialize(new
            {
                type = "external_account",
                audience =
                    "//iam.googleapis.com/projects/1/locations/global/workloadIdentityPools/pool/providers/provider",
                subject_token_type = "urn:ietf:params:oauth:token-type:jwt",
                token_url = "https://sts.example.test/v1/token",
                credential_source = new
                {
                    file = subjectPath,
                    format = new { type = "text" }
                }
            }));
        using var environment = SetGcsEnvironment(new Dictionary<string, string?>
        {
            ["GOOGLE_APPLICATION_CREDENTIALS"] = credentialPath
        });
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "sts.example.test"
                ? GcsTokenResponse("federated-token", 3600)
                : GcsObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            new PantsGcsCredentialSource.ApplicationDefault(),
            client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        Assert.Contains("subject_token=external-subject", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("requested_token_type=urn%3Aietf%3Aparams%3Aoauth%3Atoken-type%3Aaccess_token",
            handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Equal("Bearer federated-token", handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task ShouldRejectGcsTokenWithoutPositiveExpiryWithoutLeakingSecrets()
    {
        using var directory = new TemporaryDirectory();
        var credentialPath = Path.Combine(directory.Path, "authorized-user.json");
        const string clientSecret = "never-leak-client-secret";
        const string returnedToken = "never-leak-returned-token";
        await WriteAuthorizedUserAsync(
            credentialPath,
            clientSecret,
            "never-leak-refresh-token");
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "oauth.example.test"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"access_token":"{{returnedToken}}"}""")
                }
                : GcsObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(
            new PantsGcsCredentialSource.AuthorizedUserJsonFile(credentialPath),
            client);

        var exception =
            await Assert.ThrowsAsync<PantsIOException>(() => store.GetAsync("object", CancellationToken.None).AsTask());

        Assert.DoesNotContain(clientSecret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(returnedToken, exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("expires_in", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldClassifyNonObjectGcsTokenJsonAsIoFailure()
    {
        using var directory = new TemporaryDirectory();
        var credentialPath = Path.Combine(directory.Path, "authorized-user.json");
        await WriteAuthorizedUserAsync(
            credentialPath,
            "client-secret",
            "refresh-token");
        using var handler = new CredentialHttpHandler(static (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]")
        });
        using var client = new HttpClient(handler);
        var store = CreateStore(
            new PantsGcsCredentialSource.AuthorizedUserJsonFile(credentialPath),
            client);

        await Assert.ThrowsAsync<PantsIOException>(() => store.GetAsync("object", CancellationToken.None).AsTask());
    }

    [Fact]
    public void ShouldRedactGcsCredentialSecretsFromFormatting()
    {
        const string access = "render-gcs-access";
        const string secret = "render-gcs-secret";
        object[] sources =
        [
            new PantsGcsCredentialSource.BearerToken(secret),
            new PantsGcsCredentialSource.HmacKey(access, secret),
            new GcsCredential(access, secret, null)
        ];

        Assert.All(sources, source =>
        {
            Assert.DoesNotContain(access, source.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, source.ToString(), StringComparison.Ordinal);
        });
    }

    static GcsObjectStore CreateStore(
        PantsGcsCredentialSource source,
        HttpClient client) => new(
        new PantsGcsProvider(
            "bucket",
            "project",
            new Uri("https://gcs.example.test"),
            PantsGcsApiStyle.Json,
            source),
        string.Empty,
        client,
        TimeSpan.FromSeconds(5));

    static EnvironmentVariableScope SetGcsEnvironment(
        IReadOnlyDictionary<string, string?> overrides)
    {
        var values = GcsEnvironmentVariables.ToDictionary(
            static name => name,
            static _ => (string?)null,
            StringComparer.Ordinal);
        foreach (var pair in overrides)
        {
            values[pair.Key] = pair.Value;
        }

        return new EnvironmentVariableScope(values);
    }

    static async Task WriteAuthorizedUserAsync(
        string path,
        string clientSecret,
        string refreshToken) => await File.WriteAllTextAsync(
        path,
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "authorized_user",
            ["client_id"] = "client",
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
            ["token_uri"] = "https://oauth.example.test/token"
        }));

    static HttpResponseMessage GcsObjectResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("value"u8.ToArray()),
            Headers = { ETag = new EntityTagHeaderValue("\"etag\"") }
        };
        response.Headers.TryAddWithoutValidation("x-goog-generation", "1");
        return response;
    }

    static HttpResponseMessage GcsTokenResponse(string token, int expiresIn) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""{"access_token":"{{token}}","expires_in":{{expiresIn}}}""")
    };
}
