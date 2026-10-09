using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

[Collection(CredentialEnvironmentDefinition.Name)]
public sealed class GcsCredentialSourceTests
{
    static readonly string[] DevstorageScopes =
    [
        "https://www.googleapis.com/auth/devstorage.full_control"
    ];

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

    [Fact]
    public async Task ShouldFetchUrlSourcedJsonSubjectTokenWithConfiguredHeaders()
    {
        using var directory = new TemporaryDirectory();
        var credentialPath = Path.Combine(directory.Path, "external-account.json");
        await File.WriteAllTextAsync(
            credentialPath,
            ExternalAccountJson(new Dictionary<string, object?>
            {
                ["url"] = "https://subject.example.test/token",
                ["headers"] = new Dictionary<string, string>
                {
                    ["Metadata-Flavor"] = "Google",
                    ["X-Subject-Tenant"] = "tenant-42"
                },
                ["format"] = new { type = "json", subject_token_field_name = "id_token" }
            }));
        using var environment = SetGcsEnvironment(new Dictionary<string, string?>
        {
            ["GOOGLE_APPLICATION_CREDENTIALS"] = credentialPath
        });
        using var handler = new CredentialHttpHandler((request, _) => request.Uri.Host switch
        {
            "subject.example.test" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id_token":"url-subject-token"}""")
            },
            "sts.example.test" => GcsTokenResponse("federated-token", 3600),
            _ => GcsObjectResponse()
        });
        using var client = new HttpClient(handler);
        var store = CreateStore(new PantsGcsCredentialSource.ApplicationDefault(), client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        var subjectRequest = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, subjectRequest.Method);
        Assert.Equal("https://subject.example.test/token", subjectRequest.Uri.ToString());
        Assert.Equal("Google", subjectRequest.Header("Metadata-Flavor"));
        Assert.Equal("tenant-42", subjectRequest.Header("X-Subject-Tenant"));
        Assert.Contains("subject_token=url-subject-token", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Equal("Bearer federated-token", handler.Requests[2].Header("Authorization"));
    }

    [Fact]
    public async Task ShouldSendWorkforceUserProjectInStsOptions()
    {
        using var directory = new TemporaryDirectory();
        var subjectPath = Path.Combine(directory.Path, "subject-token");
        var credentialPath = Path.Combine(directory.Path, "external-account.json");
        await File.WriteAllTextAsync(subjectPath, "workforce-subject");
        await File.WriteAllTextAsync(
            credentialPath,
            ExternalAccountJson(
                new Dictionary<string, object?> { ["file"] = subjectPath },
                new Dictionary<string, object?>
                {
                    ["workforce_pool_user_project"] = "billing-project"
                }));
        using var environment = SetGcsEnvironment(new Dictionary<string, string?>
        {
            ["GOOGLE_APPLICATION_CREDENTIALS"] = credentialPath
        });
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "sts.example.test"
                ? GcsTokenResponse("workforce-token", 3600)
                : GcsObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateStore(new PantsGcsCredentialSource.ApplicationDefault(), client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        Assert.Contains(
            "options=" + Uri.EscapeDataString("{\"userProject\":\"billing-project\"}"),
            handler.Requests[0].Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldImpersonateServiceAccountWithCloudPlatformStsTokenAndDevstorageScope()
    {
        using var directory = new TemporaryDirectory();
        var subjectPath = Path.Combine(directory.Path, "subject-token");
        var credentialPath = Path.Combine(directory.Path, "external-account.json");
        await File.WriteAllTextAsync(subjectPath, "impersonation-subject");
        await File.WriteAllTextAsync(
            credentialPath,
            ExternalAccountJson(
                new Dictionary<string, object?> { ["file"] = subjectPath },
                ImpersonationExtras(900)));
        using var environment = SetGcsEnvironment(new Dictionary<string, string?>
        {
            ["GOOGLE_APPLICATION_CREDENTIALS"] = credentialPath
        });
        using var handler = new CredentialHttpHandler((request, _) => request.Uri.Host switch
        {
            "sts.example.test" => GcsTokenResponse("federated-token", 3600),
            "iam.example.test" => ImpersonationResponse("impersonated-token", "2099-01-01T00:00:00Z"),
            _ => GcsObjectResponse()
        });
        using var client = new HttpClient(handler);
        var store = CreateStore(new PantsGcsCredentialSource.ApplicationDefault(), client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        Assert.Contains(
            "scope=" + Uri.EscapeDataString("https://www.googleapis.com/auth/cloud-platform"),
            handler.Requests[0].Body,
            StringComparison.Ordinal);
        var impersonation = handler.Requests[1];
        Assert.Equal("Bearer federated-token", impersonation.Header("Authorization"));
        using var impersonationBody = JsonDocument.Parse(impersonation.Body);
        Assert.Equal("900s", impersonationBody.RootElement.GetProperty("lifetime").GetString());
        var scopes = impersonationBody.RootElement.GetProperty("scope").EnumerateArray()
            .Select(static scope => scope.GetString())
            .ToArray();
        Assert.Equal(DevstorageScopes, scopes);
        Assert.Equal("Bearer impersonated-token", handler.Requests[2].Header("Authorization"));
    }

    [Theory]
    [MemberData(nameof(UnsupportedImpersonationLifetimes))]
    public async Task ShouldRejectImpersonationLifetimeOutsideSupportedRange(object lifetime)
    {
        using var directory = new TemporaryDirectory();
        var subjectPath = Path.Combine(directory.Path, "subject-token");
        var credentialPath = Path.Combine(directory.Path, "external-account.json");
        await File.WriteAllTextAsync(subjectPath, "lifetime-subject");
        await File.WriteAllTextAsync(
            credentialPath,
            ExternalAccountJson(
                new Dictionary<string, object?> { ["file"] = subjectPath },
                ImpersonationExtras(lifetime)));
        using var environment = SetGcsEnvironment(new Dictionary<string, string?>
        {
            ["GOOGLE_APPLICATION_CREDENTIALS"] = credentialPath
        });
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "sts.example.test"
                ? GcsTokenResponse("federated-token", 3600)
                : ImpersonationResponse("impersonated-token", "2099-01-01T00:00:00Z"));
        using var client = new HttpClient(handler);
        var store = CreateStore(new PantsGcsCredentialSource.ApplicationDefault(), client);

        await Assert.ThrowsAsync<PantsInvalidArgumentException>(
            () => store.GetAsync("object", CancellationToken.None).AsTask());

        Assert.DoesNotContain(handler.Requests, static request => request.Uri.Host == "iam.example.test");
    }

    public static TheoryData<object> UnsupportedImpersonationLifetimes() => new()
    {
        599,
        43_201,
        "900",
        900.5
    };

    [Theory]
    [MemberData(nameof(InvalidImpersonationResponses))]
    public async Task ShouldRejectImpersonationResponseWithoutUsableExpiryOrToken(
        string? expireTime,
        string accessToken)
    {
        using var directory = new TemporaryDirectory();
        var subjectPath = Path.Combine(directory.Path, "subject-token");
        var credentialPath = Path.Combine(directory.Path, "external-account.json");
        await File.WriteAllTextAsync(subjectPath, "response-subject");
        await File.WriteAllTextAsync(
            credentialPath,
            ExternalAccountJson(
                new Dictionary<string, object?> { ["file"] = subjectPath },
                ImpersonationExtras(900)));
        using var environment = SetGcsEnvironment(new Dictionary<string, string?>
        {
            ["GOOGLE_APPLICATION_CREDENTIALS"] = credentialPath
        });
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host switch
            {
                "sts.example.test" => GcsTokenResponse("federated-token", 3600),
                "iam.example.test" => ImpersonationResponse(accessToken, expireTime),
                _ => GcsObjectResponse()
            });
        using var client = new HttpClient(handler);
        var store = CreateStore(new PantsGcsCredentialSource.ApplicationDefault(), client);

        var exception = await Assert.ThrowsAsync<PantsIOException>(
            () => store.GetAsync("object", CancellationToken.None).AsTask());

        Assert.DoesNotContain("never-leak-impersonated", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, static request => request.Uri.Host == "gcs.example.test");
    }

    public static TheoryData<string?, string> InvalidImpersonationResponses() => new()
    {
        { null, "never-leak-impersonated" },
        { "not-a-timestamp", "never-leak-impersonated" },
        { "2000-01-01T00:00:00Z", "never-leak-impersonated" },
        { "2099-01-01T00:00:00Z", string.Empty }
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectWhitespaceOnlySubjectTokenBeforeStsRequest(bool jsonFormat)
    {
        using var directory = new TemporaryDirectory();
        var subjectPath = Path.Combine(directory.Path, "subject-token");
        var credentialPath = Path.Combine(directory.Path, "external-account.json");
        await File.WriteAllTextAsync(subjectPath, jsonFormat ? """{"id_token":"   "}""" : " \n\t ");
        var source = jsonFormat
            ? new Dictionary<string, object?>
            {
                ["file"] = subjectPath,
                ["format"] = new { type = "json", subject_token_field_name = "id_token" }
            }
            : new Dictionary<string, object?> { ["file"] = subjectPath };
        await File.WriteAllTextAsync(credentialPath, ExternalAccountJson(source));
        using var environment = SetGcsEnvironment(new Dictionary<string, string?>
        {
            ["GOOGLE_APPLICATION_CREDENTIALS"] = credentialPath
        });
        using var handler = new CredentialHttpHandler(static (_, _) => GcsTokenResponse("unexpected", 3600));
        using var client = new HttpClient(handler);
        var store = CreateStore(new PantsGcsCredentialSource.ApplicationDefault(), client);

        await Assert.ThrowsAsync<PantsInvalidArgumentException>(
            () => store.GetAsync("object", CancellationToken.None).AsTask());

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ShouldRejectRootExecutableCredentialWithoutHttpRequests()
    {
        await AssertExternalAccountRejectedWithoutHttpAsync(
            ExternalAccountJson(
                new Dictionary<string, object?> { ["file"] = "unused" },
                new Dictionary<string, object?>
                {
                    ["executable"] = new { command = "print-token" }
                }));
    }

    [Fact]
    public async Task ShouldRejectExecutableCredentialSourceWithoutHttpRequests()
    {
        await AssertExternalAccountRejectedWithoutHttpAsync(
            ExternalAccountJson(new Dictionary<string, object?>
            {
                ["executable"] = new { command = "print-token" }
            }));
    }

    [Fact]
    public async Task ShouldRejectAwsEnvironmentIdWithUrlWithoutTreatingUrlAsSubjectToken()
    {
        await AssertExternalAccountRejectedWithoutHttpAsync(
            ExternalAccountJson(new Dictionary<string, object?>
            {
                ["environment_id"] = "aws1",
                ["url"] = "http://169.254.169.254/latest/meta-data/identity"
            }));
    }

    static async Task AssertExternalAccountRejectedWithoutHttpAsync(string credentialJson)
    {
        using var directory = new TemporaryDirectory();
        var credentialPath = Path.Combine(directory.Path, "external-account.json");
        await File.WriteAllTextAsync(credentialPath, credentialJson);
        using var environment = SetGcsEnvironment(new Dictionary<string, string?>
        {
            ["GOOGLE_APPLICATION_CREDENTIALS"] = credentialPath
        });
        using var handler = new CredentialHttpHandler(static (_, _) => GcsTokenResponse("unexpected", 3600));
        using var client = new HttpClient(handler);
        var store = CreateStore(new PantsGcsCredentialSource.ApplicationDefault(), client);

        var exception = await Assert.ThrowsAsync<PantsInvalidArgumentException>(
            () => store.GetAsync("object", CancellationToken.None).AsTask());

        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("169.254.169.254", exception.Message, StringComparison.Ordinal);
    }

    static string ExternalAccountJson(
        object credentialSource,
        IReadOnlyDictionary<string, object?>? extras = null)
    {
        var document = new Dictionary<string, object?>
        {
            ["type"] = "external_account",
            ["audience"] =
                "//iam.googleapis.com/projects/1/locations/global/workloadIdentityPools/pool/providers/provider",
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:jwt",
            ["token_url"] = "https://sts.example.test/v1/token",
            ["credential_source"] = credentialSource
        };
        if (extras is not null)
        {
            foreach (var pair in extras)
            {
                document[pair.Key] = pair.Value;
            }
        }

        return JsonSerializer.Serialize(document);
    }

    static Dictionary<string, object?> ImpersonationExtras(object lifetime) => new()
    {
        ["service_account_impersonation_url"] =
            "https://iam.example.test/v1/projects/-/serviceAccounts/service@example.test:generateAccessToken",
        ["service_account_impersonation"] = new { token_lifetime_seconds = lifetime }
    };

    static HttpResponseMessage ImpersonationResponse(string accessToken, string? expireTime) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { accessToken, expireTime }))
        };

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
