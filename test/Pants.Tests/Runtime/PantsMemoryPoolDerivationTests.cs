namespace Cntryl.Pants.Runtime;

public sealed class PantsMemoryPoolDerivationTests
{
    [Theory]
    [InlineData(64L * 1024 * 1024)]
    [InlineData(256L * 1024 * 1024)]
    [InlineData(4L * 1024 * 1024 * 1024)]
    public void ShouldReserveOneFifthOfTheBudgetForCompactionUpToTwoHundredFiftySixMebibytes(long budget)
    {
        var resolved = PantsOpenOptionsResolver.Resolve(PantsOpenOptions.Local("pool-fraction")
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget)));

        Assert.Equal(Math.Min(budget / 5, 256L * 1024 * 1024), resolved.CompactionMemoryPoolBytes);
    }

    [Theory]
    [InlineData(64L)]
    [InlineData(1_001L)]
    [InlineData(4L * 1024)]
    [InlineData(1024L * 1024)]
    [InlineData(1024L * 1024 + 1)]
    [InlineData(3L * 1024 * 1024)]
    [InlineData(6L * 1024 * 1024)]
    [InlineData(64L * 1024 * 1024)]
    [InlineData(512L * 1024 * 1024)]
    public void ShouldKeepABlockCacheForAutomaticPersistentMemtablesWithinTheBudget(long budget)
    {
        var resolved = PantsOpenOptionsResolver.Resolve(PantsOpenOptions.Local("read-share")
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget)));

        Assert.True(resolved.CompactionMemoryPoolBytes > 0);
        Assert.True(resolved.BlockCacheBytes > 0);
        Assert.True(
            resolved.TransactionMemoryPoolBytes +
            resolved.CompactionMemoryPoolBytes +
            resolved.ScanMemoryPoolBytes +
            2 * resolved.MemtableSizeLimitBytes +
            resolved.BlockCacheBytes <= budget);
    }

    [Fact]
    public void ShouldRejectPersistentExplicitMemtablesThatLeaveNoBlockCache()
    {
        const long budget = 10L * 1024 * 1024;
        var inMemory = PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget));
        var pools = PantsOpenOptionsResolver.Resolve(inMemory);
        var remainder = budget -
                        pools.TransactionMemoryPoolBytes -
                        pools.CompactionMemoryPoolBytes -
                        pools.ScanMemoryPoolBytes;
        Assert.Equal(0, remainder % 2);
        var memtableBytes = remainder / 2;

        var inMemoryResolved = PantsOpenOptionsResolver.Resolve(inMemory.WithMemtableLimits(memtableBytes));
        var error = Assert.Throws<PantsResourceLimitException>(() => PantsOpenOptionsResolver.Resolve(
            PantsOpenOptions.Local("no-cache")
                .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget))
                .WithMemtableLimits(memtableBytes)));

        Assert.Equal(0, inMemoryResolved.BlockCacheBytes);
        Assert.Contains("block cache", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(3L)]
    [InlineData(4L)]
    public void ShouldRejectAnInMemoryBudgetThatLeavesNoCompactionPool(long budget)
    {
        var error = Assert.Throws<PantsResourceLimitException>(() => PantsOpenOptionsResolver.Resolve(
            PantsOpenOptions.InMemory()
                .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget))
                .WithMemtableLimits(1)));

        Assert.Contains("compaction", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
