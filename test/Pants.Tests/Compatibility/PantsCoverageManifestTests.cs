namespace Cntryl.Pants.Tests.Compatibility;

public sealed class PantsCoverageManifestTests
{
    [Fact]
    public void ShouldKeepCoverageManifestExhaustiveGivenPublicBehaviorAxes()
    {
        Assert.Equal(
            ["Sync", "Buffered", "BestEffort", "CloudAsync", "CloudStrict"],
            Enum.GetNames<PantsDurability>());
        Assert.Equal(["Strict", "Salvage"], Enum.GetNames<PantsRecoveryPolicy>());
        Assert.Equal(["Latency", "Throughput", "Economy"], Enum.GetNames<PantsPerformanceGoal>());
        Assert.Equal(["None", "Lz4", "Zstd3", "Zstd9"], Enum.GetNames<MidgeCompressionAlgorithm>());
        Assert.Equal(
            ["AwsDefaultChain", "Environment", "SharedProfile", "StaticCredentials"],
            CredentialSourceNames<PantsS3CredentialSource>());
        Assert.Equal(
            [
                "ConnectionString",
                "EnvironmentClientSecret",
                "LightweightDefaultChain",
                "ManagedIdentity",
                "SasToken",
                "SharedKey",
                "StorageEnvironment",
                "WorkloadIdentity"
            ],
            CredentialSourceNames<PantsAzureCredentialSource>());
        Assert.Equal(
            [
                "ApplicationDefault",
                "AuthorizedUserJsonFile",
                "BearerToken",
                "HmacKey",
                "MetadataServer",
                "ServiceAccountJsonFile"
            ],
            CredentialSourceNames<PantsGcsCredentialSource>());
    }

    static string[] CredentialSourceNames<TSource>() =>
        typeof(TSource).GetNestedTypes()
            .Where(static type => !type.IsAbstract && typeof(TSource).IsAssignableFrom(type))
            .Select(static type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
