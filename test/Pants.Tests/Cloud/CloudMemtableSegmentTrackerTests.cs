namespace Pants.Tests;

public sealed class CloudMemtableSegmentTrackerTests
{
    [Fact]
    public void ShouldSelectOldestLightlyWrittenFamilyGivenBusyNeighborSegmentChurn()
    {
        var light = new ColumnFamilyIdentity(1, "light", 0);
        var busy = new ColumnFamilyIdentity(2, "busy", 0);
        var tracker = new CloudMemtableSegmentTracker();
        tracker.RecordWrite(light, currentSegmentId: 1);
        tracker.RecordWrite(busy, currentSegmentId: 3);

        var candidate = tracker.SelectFlushCandidate(currentSegmentId: 5, segmentGap: 4);

        Assert.Equal(light, candidate);
    }

    [Fact]
    public void ShouldResetFamilyGapGivenSuccessfulFlush()
    {
        var family = new ColumnFamilyIdentity(1, "family", 0);
        var tracker = new CloudMemtableSegmentTracker();
        tracker.RecordWrite(family, currentSegmentId: 1);
        tracker.RecordFlush(family);

        Assert.Null(tracker.SelectFlushCandidate(currentSegmentId: 20, segmentGap: 4));
        Assert.Equal(0UL, tracker.MaximumGap(currentSegmentId: 20));
    }

    [Fact]
    public void ShouldResetHistoricalGapGivenRecoveredNonemptyMemtable()
    {
        var family = new ColumnFamilyIdentity(1, "family", 0);
        var tracker = new CloudMemtableSegmentTracker();
        tracker.RecordWrite(family, currentSegmentId: 1);

        tracker.Reinitialize([family], currentSegmentId: 20);

        Assert.Equal(0UL, tracker.MaximumGap(currentSegmentId: 20));
        Assert.Null(tracker.SelectFlushCandidate(currentSegmentId: 20, segmentGap: 4));
    }

    [Fact]
    public void ShouldBreakEqualGapTieByLowestColumnFamilyId()
    {
        var lower = new ColumnFamilyIdentity(1, "lower", 0);
        var higher = new ColumnFamilyIdentity(2, "higher", 0);
        var tracker = new CloudMemtableSegmentTracker();
        tracker.RecordWrite(higher, currentSegmentId: 1);
        tracker.RecordWrite(lower, currentSegmentId: 1);

        var candidate = tracker.SelectFlushCandidate(currentSegmentId: 5, segmentGap: 4);

        Assert.Equal(lower, candidate);
    }
}
