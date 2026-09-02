using System.Collections.Immutable;

namespace Cntryl.Pants.Tests.Transactions;

public sealed class SnapshotReadPathTests
{
    static readonly ColumnFamilyIdentity Family = new(0, "default", 0);
    static readonly ColumnFamilyIdentity OtherFamily = new(1, "other", 0);

    [Fact]
    public void ShouldExcludePointReadCandidatesWhoseFileRangeDoesNotContainTheKey()
    {
        var inRange = File("in-range.sst", Family, Bytes(10), Bytes(20));
        var outOfRange = File("out-of-range.sst", Family, Bytes(30), Bytes(40));
        var snapshot = Snapshot(inRange, outOfRange);

        var candidates = SnapshotReadPath.ResolveCandidateFilesForPoint(snapshot, Family, [15]);

        Assert.Equal(["in-range.sst"], candidates.Select(file => file.Name));
    }

    [Fact]
    public void ShouldExcludeCandidatesFromOtherColumnFamilies()
    {
        var thisFamily = File("this-family.sst", Family, Bytes(0), Bytes(255));
        var otherFamily = File("other-family.sst", OtherFamily, Bytes(0), Bytes(255));
        var snapshot = Snapshot(thisFamily, otherFamily);

        var candidates = SnapshotReadPath.ResolveCandidateFilesForPoint(snapshot, Family, [15]);

        Assert.Equal(["this-family.sst"], candidates.Select(file => file.Name));
    }

    [Fact]
    public void ShouldOrderPointReadCandidatesNewestSstSequenceFirst()
    {
        var older = File("older.sst", Family, Bytes(0), Bytes(255), sstSequence: 1);
        var newer = File("newer.sst", Family, Bytes(0), Bytes(255), sstSequence: 2);
        var snapshot = Snapshot(older, newer);

        var candidates = SnapshotReadPath.ResolveCandidateFilesForPoint(snapshot, Family, [15]);

        Assert.Equal(["newer.sst", "older.sst"], candidates.Select(file => file.Name));
    }

    [Fact]
    public void ShouldReturnNoCandidatesGivenNoVisibleFilesForTheFamily()
    {
        var snapshot = Snapshot();

        var candidates = SnapshotReadPath.ResolveCandidateFilesForPoint(snapshot, Family, [15]);

        Assert.Empty(candidates);
    }

    [Fact]
    public void ShouldIncludeRangeCandidatesThatOverlapTheScanBoundsButExcludeDisjointFiles()
    {
        var overlapping = File("overlapping.sst", Family, Bytes(5), Bytes(15));
        var disjoint = File("disjoint.sst", Family, Bytes(100), Bytes(200));
        var snapshot = Snapshot(overlapping, disjoint);

        var candidates = SnapshotReadPath.ResolveCandidateFilesForRange(
            snapshot,
            Family,
            startInclusive: [10],
            endExclusive: [20]);

        Assert.Equal(["overlapping.sst"], candidates.Select(file => file.Name));
    }

    [Fact]
    public void ShouldTreatNullBoundsAsUnbounded()
    {
        var file = File("only.sst", Family, Bytes(5), Bytes(15));
        var snapshot = Snapshot(file);

        var candidates = SnapshotReadPath.ResolveCandidateFilesForRange(
            snapshot,
            Family,
            startInclusive: null,
            endExclusive: null);

        Assert.Equal(["only.sst"], candidates.Select(candidate => candidate.Name));
    }

    static FileMeta File(
        string name,
        ColumnFamilyIdentity family,
        byte[] smallest,
        byte[] largest,
        ulong sstSequence = 1) => new()
        {
            Name = name,
            ColumnFamilyId = family.Id,
            SmallestKey = smallest.Select(value => (int)value).ToArray(),
            LargestKey = largest.Select(value => (int)value).ToArray(),
            SstSequence = sstSequence
        };

    static byte[] Bytes(byte value) => [value];

    static DatabaseVersion Snapshot(params FileMeta[] files)
    {
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        var visibleFiles = files
            .GroupBy(file => file.ColumnFamilyId)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray());
        return state.CreateVersion(visibleFiles);
    }
}
