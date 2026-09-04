namespace Cntryl.Pants.Tests.Contracts;

public sealed class PantsConfigurationParityTests
{
    [Fact]
    public void ShouldKeepOptionsImmutableAndDeriveTheSamePoolsRegardlessOfCallOrder()
    {
        var defaults = PantsOpenOptions.Local("relative-database");
        var first = defaults
            .WithPerformanceGoal(PantsPerformanceGoal.Throughput)
            .WithWorkloadProfile(PantsWorkloadProfile.WriteHeavy)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(128L * 1024 * 1024));
        var second = defaults
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
        var latency = PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget))
            .WithPerformanceGoal(PantsPerformanceGoal.Latency)
            .WithWorkloadProfile(PantsWorkloadProfile.ReadMostly);
        var throughput = PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget))
            .WithPerformanceGoal(PantsPerformanceGoal.Throughput)
            .WithWorkloadProfile(PantsWorkloadProfile.WriteHeavy);
        var rangeScan = PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget))
            .WithPerformanceGoal(PantsPerformanceGoal.Throughput)
            .WithWorkloadProfile(PantsWorkloadProfile.RangeScan);
        var economy = PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget))
            .WithPerformanceGoal(PantsPerformanceGoal.Economy);

        Assert.True(latency.MemtableSizeLimitBytes < throughput.MemtableSizeLimitBytes);
        Assert.True(latency.BlockSizeBytes < rangeScan.BlockSizeBytes);
        Assert.True(economy.BlockCacheBytes <= 256L * 1024 * 1024);
        Assert.All(
            [latency, throughput, rangeScan, economy],
            static options => Assert.True(
                2 * options.MemtableSizeLimitBytes +
                options.TransactionMemoryPoolBytes +
                options.CompactionMemoryPoolBytes +
                options.ScanMemoryPoolBytes +
                options.BlockCacheBytes <= options.MemoryBudgetBytes));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(31)]
    [InlineData(1_024)]
    [InlineData(131_072)]
    public void ShouldAccountForEveryPoolWithinSmallExplicitBudgets(long budget)
    {
        var options = PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget));

        Assert.Equal(budget, options.MemoryBudgetBytes);
        Assert.True(options.TransactionMemoryPoolBytes > 0);
        Assert.True(options.CompactionMemoryPoolBytes >= 0);
        Assert.True(options.ScanMemoryPoolBytes >= 0);
        Assert.True(
            2 * options.MemtableSizeLimitBytes +
            options.TransactionMemoryPoolBytes +
            options.CompactionMemoryPoolBytes +
            options.ScanMemoryPoolBytes +
            options.BlockCacheBytes <= budget);
    }

    [Fact]
    public void ShouldAllocateTenPercentOfExplicitBudgetToTransactionsByDefault()
    {
        var options = PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(1_000));

        Assert.Equal(100, options.TransactionMemoryPoolBytes);
    }

    [Fact]
    public void ShouldValidateEveryExplicitConfigurationBoundary()
    {
        var valid = PantsOpenOptions.InMemory()
            .WithStorageTimeout(TimeSpan.FromMilliseconds(1))
            .WithTransactionMemoryPool(1024)
            .WithMemtableLimits(2048, 1024)
            .WithWalBufferSize(4096)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(8192))
            .WithBlockCachePolicy(PantsBlockCachePolicy.TinyLfu);

        Assert.Equal(TimeSpan.FromMilliseconds(1), valid.StorageTimeout);
        Assert.Equal(1024, valid.TransactionMemoryPoolBytes);
        Assert.Equal(2048, valid.MemtableSizeLimitBytes);
        Assert.Equal(1024, valid.MemtableFlushThresholdBytes);
        Assert.Equal(4096, valid.WalBufferSizeBytes);
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
        Assert.Throws<PantsInvalidArgumentException>(() => PantsOpenOptions.InMemory()
            .WithWalBufferSize(0));
    }

    [Fact]
    public void ShouldExposeOneValidatedLeaseTimingProfile()
    {
        var defaults = PantsOpenOptions.InMemory();
        var explicitProfile = defaults
            .WithLeaseClockSkewTolerance(TimeSpan.FromSeconds(1))
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(10));
        var maximum = defaults.WithLeaseTimeToLive(TimeSpan.MaxValue);

        Assert.Equal(TimeSpan.FromSeconds(30), defaults.LeaseTimeToLive);
        Assert.Equal(TimeSpan.FromSeconds(10), defaults.LeaseHeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), explicitProfile.LeaseTimeToLive);
        Assert.Equal(TimeSpan.FromSeconds(1), explicitProfile.LeaseClockSkewTolerance);
        Assert.Equal(TimeSpan.FromSeconds(10.0 / 3), explicitProfile.LeaseHeartbeatInterval);
        Assert.Equal(TimeSpan.MaxValue, maximum.LeaseTimeToLive);
        Assert.Equal(TimeSpan.FromSeconds(10), maximum.LeaseHeartbeatInterval);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    public void ShouldRejectLeaseTimeToLiveBelowSupportedMinimum(int milliseconds)
    {
        var error = Assert.Throws<PantsInvalidArgumentException>(() => PantsOpenOptions
            .InMemory()
            .WithLeaseTimeToLive(TimeSpan.FromMilliseconds(milliseconds)));

        Assert.Contains("LeaseTimeToLive", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void ShouldRejectLeaseClockSkewAtOrAboveTimeToLive(int skewMilliseconds)
    {
        var error = Assert.Throws<PantsInvalidArgumentException>(() => PantsOpenOptions
            .InMemory()
            .WithLeaseClockSkewTolerance(TimeSpan.Zero)
            .WithLeaseTimeToLive(TimeSpan.FromMilliseconds(10))
            .WithLeaseClockSkewTolerance(TimeSpan.FromMilliseconds(skewMilliseconds)));

        Assert.Contains("LeaseClockSkewTolerance", error.Message, StringComparison.Ordinal);
        Assert.Contains("LeaseTimeToLive", error.Message, StringComparison.Ordinal);
    }
}
