using System.Collections.Immutable;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class DatabaseVersionTests
{
    [Fact]
    public void ShouldRetainFrozenFamilyRootGivenSingleKeyPublication()
    {
        var state = CreateState(1_000);
        var family = Assert.Single(state.FamilyData.Keys);
        var before = state.CreateVersion();

        state.FamilyData[family] = state.FamilyData[family].SetItem(
            Key(1_000),
            new CellState("new"u8.ToArray(), 1_001, null));
        state.Sequence = 1_001;
        var after = state.CreateVersion();

        Assert.Equal(1_000, before.Families[family].Count);
        Assert.Equal(1_001, after.Families[family].Count);
        Assert.False(before.Families[family].ContainsKey(Key(1_000)));
        Assert.True(after.Families[family].ContainsKey(Key(1_000)));
    }

    [Fact]
    public void ShouldExposeEmptyVisibleFilesGivenNoOverload()
    {
        var state = CreateState(1);

        var version = state.CreateVersion();

        Assert.Empty(version.GetVisibleFiles(0));
    }

    [Fact]
    public void ShouldPinVisibleFilesAtSnapshotTimeIndependentOfLaterManifestChanges()
    {
        var state = CreateState(1);
        var family = Assert.Single(state.FamilyData.Keys);
        var atOpen = new Dictionary<uint, ImmutableArray<FileMeta>>
        {
            [family.Id] = [new FileMeta { Name = "000001.sst", ColumnFamilyId = family.Id }]
        };

        var opened = state.CreateVersion(atOpen);
        var afterCompaction = state.CreateVersion(
            new Dictionary<uint, ImmutableArray<FileMeta>>
            {
                [family.Id] = [new FileMeta { Name = "000002.sst", ColumnFamilyId = family.Id }]
            });

        Assert.Equal("000001.sst", Assert.Single(opened.GetVisibleFiles(family.Id)).Name);
        Assert.Equal("000002.sst", Assert.Single(afterCompaction.GetVisibleFiles(family.Id)).Name);
    }

    [Fact]
    public void ShouldKeepVersionPublicationAllocationConstantGivenFiftyTimesMoreKeys()
    {
        var small = CreateState(1_000);
        var large = CreateState(50_000);

        _ = small.CreateVersion();
        _ = large.CreateVersion();
        var smallBytes = MeasureAllocation(small.CreateVersion);
        var largeBytes = MeasureAllocation(large.CreateVersion);

        Assert.InRange(largeBytes - smallBytes, -256, 256);
    }

    [Fact]
    public void ShouldPerformBoundedPathCopyGivenSingleKeyUpdateAtFiftyThousandKeys()
    {
        var state = CreateState(50_000);
        var family = Assert.Single(state.FamilyData.Keys);
        var root = state.FamilyData[family];

        var allocated = MeasureAllocation(() => root.SetItem(
            Key(25_000),
            new CellState("updated"u8.ToArray(), 50_001, null)));

        Assert.InRange(allocated, 1, 16 * 1024);
    }

    static RuntimeState CreateState(int keyCount)
    {
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            new RuntimeTelemetry());
        var family = Assert.Single(state.FamilyData.Keys);
        var builder = ImmutableSortedDictionary.CreateBuilder<byte[], CellState>(ByteArrayComparer.Instance);
        for (var index = 0; index < keyCount; index++)
        {
            builder.Add(Key(index), new CellState([1], index + 1, null));
        }

        state.FamilyData[family] = builder.ToImmutable();
        state.Sequence = keyCount;
        return state;
    }

    static long MeasureAllocation<T>(Func<T> action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    static byte[] Key(int index) =>
    [
        (byte)(index >> 24),
        (byte)(index >> 16),
        (byte)(index >> 8),
        (byte)index
    ];
}
