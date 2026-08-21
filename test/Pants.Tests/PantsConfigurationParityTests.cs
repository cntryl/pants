namespace Pants.Tests;

public sealed class PantsConfigurationParityTests
{
    [Fact]
    public void ShouldKeepOptionsImmutableAndDeriveTheSamePoolsRegardlessOfCallOrder()
    {
        PantsOpenOptions defaults = PantsOpenOptions.Local("relative-database");
        PantsOpenOptions first = defaults
            .WithPerformanceGoal(PantsPerformanceGoal.Throughput)
            .WithWorkloadProfile(PantsWorkloadProfile.WriteHeavy)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(128L * 1024 * 1024));
        PantsOpenOptions second = defaults
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(128L * 1024 * 1024))
            .WithWorkloadProfile(PantsWorkloadProfile.WriteHeavy)
            .WithPerformanceGoal(PantsPerformanceGoal.Throughput);

        Assert.Equal(PantsPerformanceGoal.Latency, defaults.PerformanceGoal);
        Assert.Equal(PantsWorkloadProfile.Mixed, defaults.WorkloadProfile);
        Assert.Equal(first.MemoryBudgetBytes, second.MemoryBudgetBytes);
        Assert.Equal(first.TransactionMemoryPoolBytes, second.TransactionMemoryPoolBytes);
        Assert.Equal(first.MemtableSizeLimitBytes, second.MemtableSizeLimitBytes);
        Assert.Equal(first.MemtableFlushThresholdBytes, second.MemtableFlushThresholdBytes);
        Assert.Equal(first.BlockCacheBytes, second.BlockCacheBytes);
        Assert.Equal(first.BlockSizeBytes, second.BlockSizeBytes);
        Assert.Equal(first.TargetSstSizeBytes, second.TargetSstSizeBytes);
        Assert.Equal(first.WalBufferSizeBytes, second.WalBufferSizeBytes);
        Assert.Equal(first.L0CompactionTrigger, second.L0CompactionTrigger);
        Assert.Equal(
            "relative-database",
            Assert.IsType<PantsStorageConfiguration.Local>(defaults.Storage).Path);
    }

    [Fact]
    public void ShouldDeriveDistinctWorkloadAndPerformanceProfilesWithinTheBudget()
    {
        const long budget = 1024L * 1024 * 1024;
        PantsOpenOptions latency = PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget))
            .WithPerformanceGoal(PantsPerformanceGoal.Latency)
            .WithWorkloadProfile(PantsWorkloadProfile.ReadMostly);
        PantsOpenOptions throughput = PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget))
            .WithPerformanceGoal(PantsPerformanceGoal.Throughput)
            .WithWorkloadProfile(PantsWorkloadProfile.WriteHeavy);
        PantsOpenOptions rangeScan = PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget))
            .WithPerformanceGoal(PantsPerformanceGoal.Throughput)
            .WithWorkloadProfile(PantsWorkloadProfile.RangeScan);
        PantsOpenOptions economy = PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget))
            .WithPerformanceGoal(PantsPerformanceGoal.Economy);

        Assert.True(latency.MemtableSizeLimitBytes < throughput.MemtableSizeLimitBytes);
        Assert.True(latency.BlockSizeBytes < rangeScan.BlockSizeBytes);
        Assert.True(economy.BlockCacheBytes <= 256L * 1024 * 1024);
        Assert.All(
            [latency, throughput, rangeScan, economy],
            static options => Assert.True(
                (2 * options.MemtableSizeLimitBytes) +
                options.TransactionMemoryPoolBytes +
                options.BlockCacheBytes <= options.MemoryBudgetBytes));
    }

    [Fact]
    public void ShouldValidateEveryExplicitConfigurationBoundary()
    {
        PantsOpenOptions valid = PantsOpenOptions.InMemory()
            .WithStorageTimeout(TimeSpan.FromMilliseconds(1))
            .WithTransactionMemoryPool(1024)
            .WithMemtableLimits(2048, 1024)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(8192))
            .WithBlockCachePolicy(PantsBlockCachePolicy.TinyLfu);

        Assert.Equal(TimeSpan.FromMilliseconds(1), valid.StorageTimeout);
        Assert.Equal(1024, valid.TransactionMemoryPoolBytes);
        Assert.Equal(2048, valid.MemtableSizeLimitBytes);
        Assert.Equal(1024, valid.MemtableFlushThresholdBytes);
        Assert.Equal(PantsBlockCachePolicy.TinyLfu, valid.BlockCachePolicy);
        Assert.Throws<PantsInvalidArgumentException>(() => PantsOpenOptions.InMemory()
            .WithStorageTimeout(TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond - 1)));
        Assert.Throws<PantsInvalidArgumentException>(() => PantsOpenOptions.InMemory()
            .WithStorageTimeout(TimeSpan.Zero));
        Assert.Throws<PantsResourceLimitException>(() => PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(1024))
            .WithTransactionMemoryPool(1025));
        Assert.Throws<PantsInvalidArgumentException>(() => PantsOpenOptions.InMemory()
            .WithMemtableLimits(1024, 1025));
    }
}
