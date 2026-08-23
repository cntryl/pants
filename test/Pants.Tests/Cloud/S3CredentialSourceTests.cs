using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace Cntryl.Pants.Tests.Cloud;

[Collection(CredentialEnvironmentDefinition.Name)]
public sealed class S3CredentialSourceTests
{
    static readonly string[] AwsEnvironmentVariables =
    [
        "AWS_ACCESS_KEY_ID",
        "AWS_ACCESS_KEY",
        "AWS_SECRET_ACCESS_KEY",
        "AWS_SECRET_KEY",
        "AWS_SESSION_TOKEN",
        "AWS_SECURITY_TOKEN",
        "AWS_PROFILE",
        "AWS_SHARED_CREDENTIALS_FILE",
        "AWS_CONFIG_FILE",
        "AWS_ROLE_ARN",
        "AWS_ROLE_SESSION_NAME",
        "AWS_WEB_IDENTITY_TOKEN_FILE",
        "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI",
        "AWS_CONTAINER_CREDENTIALS_FULL_URI",
        "AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE",
        "AWS_CONTAINER_AUTHORIZATION_TOKEN",
        "AWS_EC2_METADATA_DISABLED",
        "AWS_EC2_METADATA_SERVICE_ENDPOINT",
        "AWS_EC2_METADATA_SERVICE_ENDPOINT_MODE"
    ];

    [Fact]
    public async Task ShouldResolveExplicitAwsEnvironmentCredentials()
    {
        using var environment = SetAwsEnvironment(new Dictionary<string, string?>
        {
            ["AWS_ACCESS_KEY_ID"] = "environment-access",
            ["AWS_SECRET_ACCESS_KEY"] = "environment-secret",
            ["AWS_SECURITY_TOKEN"] = "environment-session"
        });
        using var handler = new CredentialHttpHandler(static (_, _) => S3ObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateS3CompatibleStore(
            new PantsS3CredentialSource.Environment(),
            client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Contains("Credential=environment-access/", request.Header("Authorization"), StringComparison.Ordinal);
        Assert.Equal("environment-session", request.Header("x-amz-security-token"));
    }

    [Fact]
    public async Task ShouldUseAwsChinaDnsSuffixForS3Requests()
    {
        using var handler = new CredentialHttpHandler(static (_, _) => S3ObjectResponse());
        using var client = new HttpClient(handler);
        var store = new S3ObjectStore(
            new PantsCloudProviderConfiguration.AwsS3(
                "bucket",
                "cn-north-1",
                new PantsS3CredentialSource.StaticCredentials("access", "secret")),
            string.Empty,
            client,
            TimeSpan.FromSeconds(5));

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        Assert.Equal(
            "bucket.s3.cn-north-1.amazonaws.com.cn",
            Assert.Single(handler.Requests).Uri.Host);
    }

    [Fact]
    public async Task ShouldResolveNamedProfileFromAwsConfigFile()
    {
        using var directory = new TemporaryDirectory();
        var configPath = Path.Combine(directory.Path, "config");
        await File.WriteAllTextAsync(
            configPath,
            "[profile qualification]\naccess_key_id = config-access\nsecret_access_key = config-secret\naws_security_token = config-session\n");
        using var handler = new CredentialHttpHandler(static (_, _) => S3ObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateS3CompatibleStore(
            new PantsS3CredentialSource.SharedProfile(
                "qualification",
                Path.Combine(directory.Path, "missing"),
                configPath),
            client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Contains("Credential=config-access/", request.Header("Authorization"), StringComparison.Ordinal);
        Assert.Equal("config-session", request.Header("x-amz-security-token"));
    }

    [Fact]
    public void ShouldRejectAwsDefaultChainForS3CompatibleProvider()
    {
        using var client = new HttpClient(new CredentialHttpHandler(static (_, _) => S3ObjectResponse()));

        var exception = Assert.Throws<PantsInvalidArgumentException>(() =>
            CreateS3CompatibleStore(new PantsS3CredentialSource.AwsDefaultChain(), client));

        Assert.Contains("AWS S3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldContinueAwsDefaultChainToProfileGivenPartialEnvironmentCredentials()
    {
        using var directory = new TemporaryDirectory();
        var credentialsPath = Path.Combine(directory.Path, "credentials");
        await File.WriteAllTextAsync(
            credentialsPath,
            "[default]\naws_access_key_id = profile-access\naws_secret_access_key = profile-secret\n");
        using var environment = SetAwsEnvironment(new Dictionary<string, string?>
        {
            ["AWS_ACCESS_KEY_ID"] = "partial-access",
            ["AWS_SHARED_CREDENTIALS_FILE"] = credentialsPath,
            ["AWS_CONFIG_FILE"] = Path.Combine(directory.Path, "missing-config"),
            ["AWS_EC2_METADATA_DISABLED"] = "true"
        });
        using var handler = new CredentialHttpHandler(static (_, _) => S3ObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateAwsStore(new PantsS3CredentialSource.AwsDefaultChain(), client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        Assert.Contains(
            "Credential=profile-access/",
            Assert.Single(handler.Requests).Header("Authorization"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldRefreshAwsWebIdentityCredentialsBeforeExpiry()
    {
        using var directory = new TemporaryDirectory();
        var tokenPath = Path.Combine(directory.Path, "token");
        await File.WriteAllTextAsync(tokenPath, "federated-secret");
        using var environment = SetAwsEnvironment(new Dictionary<string, string?>
        {
            ["AWS_ROLE_ARN"] = "arn:aws:iam::123456789012:role/qualification",
            ["AWS_ROLE_SESSION_NAME"] = "pants-qualification",
            ["AWS_WEB_IDENTITY_TOKEN_FILE"] = tokenPath,
            ["AWS_SHARED_CREDENTIALS_FILE"] = Path.Combine(directory.Path, "missing"),
            ["AWS_CONFIG_FILE"] = Path.Combine(directory.Path, "missing-config")
        });
        var issued = 0;
        using var handler = new CredentialHttpHandler((request, _) =>
        {
            if (request.Uri.Host.StartsWith("sts.", StringComparison.Ordinal))
            {
                issued++;
                Assert.DoesNotContain("federated-secret", request.Uri.AbsoluteUri, StringComparison.Ordinal);
                Assert.Contains("WebIdentityToken=federated-secret", request.Body, StringComparison.Ordinal);
                return StsResponse(
                    $"temporary-access-{issued}",
                    $"temporary-secret-{issued}",
                    $"temporary-token-{issued}",
                    DateTimeOffset.UtcNow.AddMinutes(4));
            }

            return S3ObjectResponse();
        });
        using var client = new HttpClient(handler);
        var store = CreateAwsStore(new PantsS3CredentialSource.AwsDefaultChain(), client);

        Assert.NotNull(await store.GetAsync("first", CancellationToken.None));
        Assert.NotNull(await store.GetAsync("second", CancellationToken.None));

        Assert.Equal(2, issued);
        var objectRequests = handler.Requests.Where(static request =>
            request.Uri.Host.EndsWith("amazonaws.com", StringComparison.Ordinal) &&
            !request.Uri.Host.StartsWith("sts.", StringComparison.Ordinal)).ToArray();
        Assert.Contains("Credential=temporary-access-1/", objectRequests[0].Header("Authorization"),
            StringComparison.Ordinal);
        Assert.Contains("Credential=temporary-access-2/", objectRequests[1].Header("Authorization"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldNotLeakAwsCredentialEndpointResponseBodyGivenFailure()
    {
        using var directory = new TemporaryDirectory();
        var tokenPath = Path.Combine(directory.Path, "token");
        await File.WriteAllTextAsync(tokenPath, "web-identity-secret");
        using var environment = SetAwsEnvironment(new Dictionary<string, string?>
        {
            ["AWS_ROLE_ARN"] = "arn:aws:iam::123456789012:role/qualification",
            ["AWS_WEB_IDENTITY_TOKEN_FILE"] = tokenPath,
            ["AWS_SHARED_CREDENTIALS_FILE"] = Path.Combine(directory.Path, "missing"),
            ["AWS_CONFIG_FILE"] = Path.Combine(directory.Path, "missing-config")
        });
        const string responseSecret = "provider-response-secret";
        using var handler = new CredentialHttpHandler(static (_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(responseSecret)
            });
        using var client = new HttpClient(handler);
        var store = CreateAwsStore(new PantsS3CredentialSource.AwsDefaultChain(), client);

        var exception =
            await Assert.ThrowsAsync<PantsIOException>(() => store.GetAsync("object", CancellationToken.None).AsTask());

        Assert.DoesNotContain(responseSecret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("web-identity-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldClassifyNonObjectAwsCredentialJsonAsIoFailure()
    {
        using var directory = new TemporaryDirectory();
        using var environment = SetAwsEnvironment(new Dictionary<string, string?>
        {
            ["AWS_CONTAINER_CREDENTIALS_FULL_URI"] = "http://127.0.0.1/credentials",
            ["AWS_SHARED_CREDENTIALS_FILE"] = Path.Combine(directory.Path, "missing"),
            ["AWS_CONFIG_FILE"] = Path.Combine(directory.Path, "missing-config")
        });
        using var handler = new CredentialHttpHandler(static (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]")
        });
        using var client = new HttpClient(handler);
        var store = CreateAwsStore(new PantsS3CredentialSource.AwsDefaultChain(), client);

        await Assert.ThrowsAsync<PantsIOException>(() => store.GetAsync("object", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ShouldResolveAwsContainerCredentialsWithAuthorizationTokenFile()
    {
        using var directory = new TemporaryDirectory();
        var authorizationPath = Path.Combine(directory.Path, "authorization");
        await File.WriteAllTextAsync(authorizationPath, "container-authorization");
        using var environment = SetAwsEnvironment(new Dictionary<string, string?>
        {
            ["AWS_CONTAINER_CREDENTIALS_FULL_URI"] = "http://127.0.0.1/credentials",
            ["AWS_CONTAINER_AUTHORIZATION_TOKEN"] = "lower-priority-token",
            ["AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE"] = authorizationPath,
            ["AWS_SHARED_CREDENTIALS_FILE"] = Path.Combine(directory.Path, "missing"),
            ["AWS_CONFIG_FILE"] = Path.Combine(directory.Path, "missing-config")
        });
        using var handler = new CredentialHttpHandler((request, _) =>
            request.Uri.Host == "127.0.0.1"
                ? TemporaryAwsJsonResponse("ecs-access", "ecs-secret", "ecs-token")
                : S3ObjectResponse());
        using var client = new HttpClient(handler);
        var store = CreateAwsStore(new PantsS3CredentialSource.AwsDefaultChain(), client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        var credentialRequest = handler.Requests[0];
        Assert.Equal("container-authorization", credentialRequest.Header("Authorization"));
        Assert.Contains("Credential=ecs-access/", handler.Requests[1].Header("Authorization"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldUseImdsv2WithoutFallingBackToImdsv1()
    {
        using var directory = new TemporaryDirectory();
        using var environment = SetAwsEnvironment(new Dictionary<string, string?>
        {
            ["AWS_SHARED_CREDENTIALS_FILE"] = Path.Combine(directory.Path, "missing"),
            ["AWS_CONFIG_FILE"] = Path.Combine(directory.Path, "missing-config"),
            ["AWS_EC2_METADATA_SERVICE_ENDPOINT"] = "http://169.254.169.254"
        });
        using var handler = new CredentialHttpHandler((request, _) => request.Uri.AbsolutePath switch
        {
            "/latest/api/token" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("imdsv2-token")
            },
            "/latest/meta-data/iam/security-credentials/" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("qualification-role\n")
            },
            "/latest/meta-data/iam/security-credentials/qualification-role" =>
                TemporaryAwsJsonResponse("imds-access", "imds-secret", "imds-session"),
            _ => S3ObjectResponse()
        });
        using var client = new HttpClient(handler);
        var store = CreateAwsStore(new PantsS3CredentialSource.AwsDefaultChain(), client);

        Assert.NotNull(await store.GetAsync("object", CancellationToken.None));

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("21600", handler.Requests[0].Header("X-aws-ec2-metadata-token-ttl-seconds"));
        Assert.All(
            handler.Requests.Skip(1).Take(2),
            request => Assert.Equal("imdsv2-token", request.Header("X-aws-ec2-metadata-token")));
    }

    [Fact]
    public void ShouldRedactEveryS3SecretBearingCredentialSource()
    {
        const string access = "render-access-secret";
        const string secret = "render-secret-key";
        const string session = "render-session-token";

        object[] credentials =
        [
            new PantsS3CredentialSource.StaticCredentials(access, secret, session),
            new S3Credentials(access, secret, session),
            new CachedS3Credentials(
                new S3Credentials(access, secret, session),
                DateTimeOffset.UtcNow.AddHours(1))
        ];

        Assert.All(credentials, credential =>
        {
            var rendered = credential.ToString();
            Assert.DoesNotContain(access, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(session, rendered, StringComparison.Ordinal);
        });
    }

    static S3ObjectStore CreateAwsStore(PantsS3CredentialSource source, HttpClient client) => new(
        new PantsCloudProviderConfiguration.AwsS3("bucket", "us-east-1", source),
        string.Empty,
        client,
        TimeSpan.FromSeconds(5));

    static S3ObjectStore CreateS3CompatibleStore(
        PantsS3CredentialSource source,
        HttpClient client) => new(
        new PantsCloudProviderConfiguration.S3Compatible(
            "bucket",
            "us-east-1",
            new Uri("https://objects.example.test"),
            true,
            source),
        string.Empty,
        client,
        TimeSpan.FromSeconds(5));

    static EnvironmentVariableScope SetAwsEnvironment(
        IReadOnlyDictionary<string, string?> overrides)
    {
        var values = AwsEnvironmentVariables.ToDictionary(
            static name => name,
            static _ => (string?)null,
            StringComparer.Ordinal);
        foreach (var pair in overrides)
        {
            values[pair.Key] = pair.Value;
        }

        return new EnvironmentVariableScope(values);
    }

    static HttpResponseMessage S3ObjectResponse() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent("value"u8.ToArray()),
        Headers = { ETag = new EntityTagHeaderValue("\"etag\"") }
    };

    static HttpResponseMessage StsResponse(
        string accessKey,
        string secretKey,
        string sessionToken,
        DateTimeOffset expiration) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
            $"<AssumeRoleWithWebIdentityResponse><Credentials><AccessKeyId>{accessKey}</AccessKeyId><SecretAccessKey>{secretKey}</SecretAccessKey><SessionToken>{sessionToken}</SessionToken><Expiration>{expiration.ToString("O", CultureInfo.InvariantCulture)}</Expiration></Credentials></AssumeRoleWithWebIdentityResponse>")
        };

    static HttpResponseMessage TemporaryAwsJsonResponse(
        string accessKey,
        string secretKey,
        string sessionToken) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
            $$"""
              {
                "AccessKeyId": "{{accessKey}}",
                "SecretAccessKey": "{{secretKey}}",
                "Token": "{{sessionToken}}",
                "Expiration": "{{DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture)}}"
              }
              """)
        };
}
