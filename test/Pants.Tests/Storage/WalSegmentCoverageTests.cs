using Cntryl.Pants.Storage.Internal.Wal;

namespace Cntryl.Pants.Storage;

public sealed class WalSegmentCoverageTests
{
    [Fact]
    public void ShouldPruneEveryCoveredSegmentWhenWriterEpochsAreMonotonic()
    {
        var prunable = WalSegmentCoverage.SelectPrunable(
        [
            new WalSegmentCoverageResult(false, 1, 1),
            new WalSegmentCoverageResult(true, 1, 2),
            new WalSegmentCoverageResult(true, 2, 2)
        ]);

        Assert.Equal([false, true, true], prunable);
    }

    [Fact]
    public void ShouldRetainCoveredSegmentWhenLaterRetainedSegmentHoldsLowerEpoch()
    {
        var prunable = WalSegmentCoverage.SelectPrunable(
        [
            new WalSegmentCoverageResult(true, 2, 2),
            new WalSegmentCoverageResult(false, 1, 1)
        ]);

        Assert.Equal([false, false], prunable);
    }
}
