namespace Cntryl.Pants.Tests.Contracts;

public sealed class PantsConfigurationContractTests
{
    [Fact]
    public void ShouldDeriveMemoryPoolsWithoutExceedingExplicitBudget()
    {
        const long budget = 64L * 1024 * 1024;
        var options = PantsOpenOptions.InMemory()
            .WithPerformanceGoal(PantsPerformanceGoal.Throughput)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget));

        Assert.Equal(budget, options.MemoryBudgetBytes);
        Assert.True(2 * options.MemtableSizeLimitBytes + options.TransactionMemoryPoolBytes <= budget);
        Assert.True(options.BlockCacheBytes <= budget);
    }

    [Fact]
    public void ShouldRejectInvalidMemoryAndTimeoutConfiguration()
    {
        var memoryError = Assert.ThrowsAny<PantsException>(() => PantsOpenOptions
            .InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(2)));
        var timeoutError = Assert.ThrowsAny<PantsException>(() => PantsOpenOptions
            .InMemory()
            .WithStorageTimeout(TimeSpan.Zero));

        Assert.Equal(PantsErrorCode.ResourceLimit, memoryError.Code);
        Assert.Equal(PantsErrorCode.InvalidArgument, timeoutError.Code);
        Assert.IsType<PantsResourceLimitException>(memoryError);
        Assert.IsType<PantsInvalidArgumentException>(timeoutError);
    }

    [Fact]
    public void ShouldModelThreeIndependentCloudLocations()
    {
        var credentials = new PantsS3CredentialSource.Environment();
        var wal = new PantsCloudStorageLocation(
            new PantsCloudProviderConfiguration.AwsS3("wal", "us-east-1", credentials),
            "database-a");
        var sst = new PantsCloudStorageLocation(
            new PantsCloudProviderConfiguration.AwsS3("sst", "us-east-1", credentials),
            "database-a");
        var control = new PantsCloudStorageLocation(
            new PantsCloudProviderConfiguration.AwsS3("control", "us-east-1", credentials),
            "database-a");
        var topology = PantsCloudStorageTopology.Shared(wal)
            .WithSst(sst)
            .WithControl(control);
        var options = PantsOpenOptions.CloudMulti("./cache", topology);

        var storage = Assert.IsType<PantsStorageConfiguration.Cloud>(options.Storage);
        Assert.Equal("wal", Assert.IsType<PantsCloudProviderConfiguration.AwsS3>(storage.Topology.Wal.Provider).Bucket);
        Assert.Equal("sst", Assert.IsType<PantsCloudProviderConfiguration.AwsS3>(storage.Topology.Sst.Provider).Bucket);
        Assert.Equal(
            "control",
            Assert.IsType<PantsCloudProviderConfiguration.AwsS3>(storage.Topology.Control.Provider).Bucket);
    }

    [Fact]
    public void ShouldRedactEveryInlineCloudSecretFromFormatting()
    {
        (object Value, string SensitiveValue)[] secrets =
        {
            (new PantsS3CredentialSource.StaticCredentials("AKIA-RAW", "S3-RAW", "SESSION-RAW"), "S3-RAW"),
            (new PantsAzureCredentialSource.SharedKey("AZURE-RAW"), "AZURE-RAW"),
            (new PantsAzureCredentialSource.SasToken("SAS-RAW"), "SAS-RAW"),
            (new PantsAzureCredentialSource.ConnectionString("CONNECTION-RAW"), "CONNECTION-RAW"),
            (new PantsGcsCredentialSource.BearerToken("BEARER-RAW"), "BEARER-RAW"),
            (new PantsGcsCredentialSource.HmacKey("HMAC-ID-RAW", "HMAC-RAW"), "HMAC-RAW")
        };

        foreach (var (value, sensitiveValue) in secrets)
        {
            var formatted = value.ToString()!;
            Assert.Contains("REDACTED", formatted, StringComparison.Ordinal);
            Assert.DoesNotContain(sensitiveValue, formatted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ShouldKeepCloudObjectClassesDisjoint()
    {
        string[] prefixes =
        {
            PantsCloudObjectLayout.WalPrefix,
            PantsCloudObjectLayout.SstPrefix,
            PantsCloudObjectLayout.MetadataPrefix
        };

        Assert.Equal(prefixes.Length, prefixes.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain('/', PantsCloudObjectLayout.LeaseObjectKey);
        Assert.StartsWith(PantsCloudObjectLayout.MetadataPrefix, PantsCloudObjectLayout.DdlRegistryObjectKey);
        Assert.Equal(
            "wal/epochs/00000000000000000007/00000000000000000011.wal",
            PantsCloudObjectLayout.WalSegmentObjectKey(7, 11));
    }
}
