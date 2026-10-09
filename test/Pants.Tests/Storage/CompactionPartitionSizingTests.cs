using Cntryl.Pants.Storage.Internal.Compaction.Compaction;

namespace Cntryl.Pants.Storage;

public sealed class CompactionPartitionSizingTests
{
    [Fact]
    public void ShouldSizeTheOutputPartitionFromTheRemainderLeftByTheInputs()
    {
        const long available = 2L * 1024 * 1024;

        var target = CompactionPartitionSizing.OutputPartitionTargetBytes(long.MaxValue / 4, available);

        Assert.Equal(available - (available / 8), target);
    }

    [Fact]
    public void ShouldKeepTheRequestedTargetWhenItIsSmallerThanWhatRemains()
    {
        var target = CompactionPartitionSizing.OutputPartitionTargetBytes(2048, 2L * 1024 * 1024);

        Assert.Equal(2048, target);
    }

    [Fact]
    public void ShouldRejectAnAvailableRemainderThatCannotHoldAMinimalOutputPartition()
    {
        Assert.Throws<PantsResourceLimitException>(() =>
            CompactionPartitionSizing.OutputPartitionTargetBytes(1024 * 1024, 4 * 1024));
    }
}
