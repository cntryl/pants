using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Contracts;

public sealed class PantsResolvedConfigurationTests
{
    [Fact]
    public async Task ShouldReportResolvedValuesMatchingRuntimeMetricsAfterOpen()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithPerformanceGoal(PantsPerformanceGoal.Throughput)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(512L * 1024 * 1024))
            .WithBackgroundCompaction(false);

        var resolved = PantsOpenOptionsResolver.Resolve(options);

        await using var database = await PantsDatabase.OpenAsync(options);
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(metrics.MemtableSizeLimitBytes, resolved.MemtableSizeLimitBytes);
        Assert.Equal(metrics.MemtableFlushThresholdBytes, resolved.MemtableFlushThresholdBytes);
        Assert.Equal(metrics.BlockCacheCapacityBytes, resolved.BlockCacheBytes);
        Assert.Equal(metrics.CompactionBufferCapacityBytes, resolved.CompactionMemoryPoolBytes);
        Assert.Equal(metrics.ScanBufferCapacityBytes, resolved.ScanMemoryPoolBytes);
        Assert.Equal(512L * 1024 * 1024, resolved.MemoryBudgetBytes);
        Assert.Equal(512L * 1024 * 1024, resolved.TargetSstSizeBytes);
        Assert.Equal(64 * 1024, resolved.BlockSizeBytes);
        Assert.Equal(6, resolved.L0CompactionTrigger);
    }

    [Fact]
    public void ShouldResolveDerivedRuntimeResponseTimeoutAndPreserveExplicitValues()
    {
        var derived = PantsOpenOptionsResolver.Resolve(PantsOpenOptions.InMemory());
        Assert.Equal(TimeSpan.FromSeconds(30), derived.StorageTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), derived.RuntimeResponseTimeout);

        var explicitTimeout = PantsOpenOptionsResolver.Resolve(PantsOpenOptions.InMemory()
            .WithRuntimeResponseTimeout(TimeSpan.FromSeconds(90)));
        Assert.Equal(TimeSpan.FromSeconds(90), explicitTimeout.RuntimeResponseTimeout);
    }

    [Fact]
    public void ShouldRejectOptionsThatCannotBeResolved()
    {
        var error = Assert.ThrowsAny<PantsException>(() => PantsOpenOptionsResolver.Resolve(
            PantsOpenOptions.InMemory().WithMemoryBudget(PantsMemoryBudget.FromBytes(2))));

        Assert.Equal(PantsErrorCode.ResourceLimit, error.Code);
    }
}
