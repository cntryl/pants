namespace Cntryl.Pants.Cloud;

public sealed class CloudMemtableSegmentTrackerTests
{
    [Fact]
    public void ShouldSelectOldestLightlyWrittenFamilyGivenBusyNeighborSegmentChurn()
    {
        var light = new ColumnFamilyIdentity(1, "light", 0);
        var busy = new ColumnFamilyIdentity(2, "busy", 0);
        var tracker = new CloudMemtableSegmentTracker();
        tracker.RecordWrite(light, 1);
        tracker.RecordWrite(busy, 3);

        var candidate = tracker.SelectFlushCandidate(5, 4);

        Assert.Equal(light, candidate);
    }

    [Fact]
    public void ShouldResetFamilyGapGivenSuccessfulFlush()
    {
        var family = new ColumnFamilyIdentity(1, "family", 0);
        var tracker = new CloudMemtableSegmentTracker();
        tracker.RecordWrite(family, 1);
        tracker.RecordFlush(family);

        Assert.Null(tracker.SelectFlushCandidate(20, 4));
        Assert.Equal(0UL, tracker.MaximumGap(20));
    }

    [Fact]
    public void ShouldResetHistoricalGapGivenRecoveredNonemptyMemtable()
    {
        var family = new ColumnFamilyIdentity(1, "family", 0);
        var tracker = new CloudMemtableSegmentTracker();
        tracker.RecordWrite(family, 1);

        tracker.Reinitialize([family], 20);

        Assert.Equal(0UL, tracker.MaximumGap(20));
        Assert.Null(tracker.SelectFlushCandidate(20, 4));
    }

    [Fact]
    public void ShouldBreakEqualGapTieByLowestColumnFamilyId()
    {
        var lower = new ColumnFamilyIdentity(1, "lower", 0);
        var higher = new ColumnFamilyIdentity(2, "higher", 0);
        var tracker = new CloudMemtableSegmentTracker();
        tracker.RecordWrite(higher, 1);
        tracker.RecordWrite(lower, 1);

        var candidate = tracker.SelectFlushCandidate(5, 4);

        Assert.Equal(lower, candidate);
    }
}
