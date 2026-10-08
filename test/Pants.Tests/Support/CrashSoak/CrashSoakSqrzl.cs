using Xunit.Sdk;

namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>The Sqrzl emulator started by <c>docker compose up -d sqrzl</c>, reached through its S3 API.</summary>
static class CrashSoakSqrzl
{
    const string Bucket = "pants-sqrzl-s3";

    static readonly HttpClient HealthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    static string Endpoint
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("SQRZL_ENDPOINT");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.TrimEnd('/');
            }

            var port = Environment.GetEnvironmentVariable("SQRZL_API_PORT");
            return $"http://127.0.0.1:{(string.IsNullOrWhiteSpace(port) ? "9000" : port)}";
        }
    }

    public static PantsCloudStorageLocation CreateLocation(string prefix) => new(
        new PantsS3CompatibleProvider(
            Bucket,
            "us-east-1",
            new Uri(Endpoint),
            true,
            new PantsS3CredentialSource.StaticCredentials("admin", "easy-peasy")),
        prefix);

    public static async Task RequireAsync()
    {
        try
        {
            using var response = await HealthClient.GetAsync(
                $"{Endpoint}/healthz",
                HttpCompletionOption.ResponseHeadersRead);
            if (response.IsSuccessStatusCode)
            {
                return;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new XunitException(
                $"The Sqrzl crash/soak backend requires a running emulator at {Endpoint}. " +
                "Start it with 'docker compose up -d sqrzl'.",
                exception);
        }

        throw new XunitException($"The Sqrzl crash/soak backend requires a healthy emulator at {Endpoint}.");
    }
}
