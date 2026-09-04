namespace Cntryl.Pants.Tests.Storage;

/// <summary>
/// Slice 6a (issue #219): <see cref="PantsOpenOptions"/> already derives a transaction pool and
/// a memtable-for-two-generations limit from a single memory budget (mirroring Midge's
/// <c>derive_memory_pools</c>); this covers the addition of an explicit, bounded compaction
/// memory pool alongside them.
/// </summary>
public sealed class PantsMemoryBudgetSplitTests
{
    [Theory]
    [InlineData(64L * 1024 * 1024)]
    [InlineData(256L * 1024 * 1024)]
    [InlineData(4L * 1024 * 1024 * 1024)]
    public void ShouldCapCompactionMemoryPoolAtOneTenthOfBudgetOrTwoHundredFiftySixMebibytes(long budget)
    {
        var options = PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget));

        var expected = Math.Min(budget / 10, 256L * 1024 * 1024);
        Assert.Equal(Math.Max(1, expected), options.CompactionMemoryPoolBytes);
    }

    [Fact]
    public void ShouldNotLetTheDerivedPoolsExceedTheConfiguredBudget()
    {
        const long budget = 96L * 1024 * 1024;
        var options = PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget));

        Assert.True(
            options.TransactionMemoryPoolBytes +
            options.CompactionMemoryPoolBytes +
            options.ScanMemoryPoolBytes +
            2 * options.MemtableSizeLimitBytes +
            options.BlockCacheBytes <= budget);
    }

    [Fact]
    public void ShouldReserveEnoughCompactionMemoryForDecodedBlocksUnderASubMebibyteBudget()
    {
        const long budget = 64L * 1024;
        var options = PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget));

        Assert.True(options.CompactionMemoryPoolBytes > budget / 2);
        Assert.True(
            options.TransactionMemoryPoolBytes +
            options.CompactionMemoryPoolBytes +
            options.ScanMemoryPoolBytes +
            2 * options.MemtableSizeLimitBytes +
            options.BlockCacheBytes <= budget);
    }

    [Fact]
    public void ShouldUseUnallocatedExplicitBudgetForCompactionInputAndOutputBuffers()
    {
        const long budget = 2L * 1024 * 1024;
        var options = PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budget))
            .WithMemtableLimits(512 * 1024)
            .WithTransactionMemoryPool(512 * 1024);

        Assert.True(options.CompactionMemoryPoolBytes > 300 * 1024);
        Assert.True(
            options.TransactionMemoryPoolBytes +
            options.CompactionMemoryPoolBytes +
            options.ScanMemoryPoolBytes +
            2 * options.MemtableSizeLimitBytes +
            options.BlockCacheBytes <= budget);
    }

    [Fact]
    public void ShouldDeriveASaneCompactionPoolUnderAutoBudget()
    {
        var options = PantsOpenOptions.InMemory();

        Assert.True(options.CompactionMemoryPoolBytes > 0);
        Assert.True(options.CompactionMemoryPoolBytes <= 256L * 1024 * 1024);
        Assert.True(options.ScanMemoryPoolBytes > 0);
        Assert.True(options.ScanMemoryPoolBytes <= 128L * 1024 * 1024);
    }
}
