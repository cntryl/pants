using System.Runtime.CompilerServices;

namespace Cntryl.Pants.Tests.Runtime;

public sealed class RuntimeStateFlushReleaseTests
{
    static readonly ColumnFamilyIdentity Family = new(0, "default", 0);

    [Fact]
    public void ShouldRemoveOnlyKeysCoveredByTheFlushedGeneration()
    {
        var state = NewState();
        state.FamilyData[Family] = state.FamilyData[Family]
            .SetItem(Key(1), new CellState([1], 1, null))
            .SetItem(Key(2), new CellState([2], 2, null));

        state.ReleaseFlushedGeneration(Flush([Mutation(1, 1)]));

        Assert.False(state.FamilyData[Family].ContainsKey(Key(1)));
        Assert.True(state.FamilyData[Family].ContainsKey(Key(2)));
    }

    [Fact]
    public void ShouldNotRemoveAKeyThatWasOverwrittenAfterTheFlushWasTaken()
    {
        var state = NewState();
        state.FamilyData[Family] = state.FamilyData[Family].SetItem(
            Key(1),
            new CellState([9], 5, null));

        // The flush captured sequence 1 for this key, but a newer write (sequence 5) landed
        // afterwards; releasing must not discard the newer in-memory value.
        state.ReleaseFlushedGeneration(Flush([Mutation(1, 1)]));

        Assert.True(state.FamilyData[Family].TryGetValue(Key(1), out var cell));
        Assert.Equal(5, cell!.WriteSequence);
    }

    [Fact]
    public void ShouldLeaveAnOlderSnapshotsRootUntouchedGivenStructuralSharing()
    {
        var state = NewState();
        state.FamilyData[Family] = state.FamilyData[Family].SetItem(
            Key(1),
            new CellState([1], 1, null));
        var olderSnapshot = state.CreateVersion();

        state.ReleaseFlushedGeneration(Flush([Mutation(1, 1)]));

        Assert.True(olderSnapshot.Families[Family].ContainsKey(Key(1)));
        Assert.False(state.FamilyData[Family].ContainsKey(Key(1)));
    }

    [Fact]
    public void ShouldBeANoOpGivenAnUnknownColumnFamily()
    {
        var state = NewState();

        var unknownFamily = new ColumnFamilyIdentity(99, "missing", 0);
        state.ReleaseFlushedGeneration(new FrozenMemtableFlush(
            1,
            unknownFamily,
            unknownFamily.Id,
            [Mutation(1, 1)],
            1,
            1,
            0));

        Assert.True(state.FamilyData.ContainsKey(Family));
    }

    [Fact]
    public void ShouldReleaseRangeDeletedCellsAndTombstonesCoveredByTheFlush()
    {
        var state = NewState();
        state.FamilyData[Family] = state.FamilyData[Family]
            .SetItem(Key(1), new CellState(null, 3, null))
            .SetItem(Key(2), new CellState([2], 4, null));
        state.RangeTombstones[Family] =
        [
            new CommittedRangeTombstone(Key(0), Key(2), 3)
        ];
        var rangeDelete = new WalMutation(
            Family.Id,
            WalOperation.DeleteRange,
            Key(0),
            null,
            3,
            null,
            Key(2));

        state.ReleaseFlushedGeneration(Flush([rangeDelete]));

        Assert.False(state.FamilyData[Family].ContainsKey(Key(1)));
        Assert.True(state.FamilyData[Family].ContainsKey(Key(2)));
        Assert.Empty(state.RangeTombstones[Family]);
    }

    [Fact]
    public void ShouldMakeAReleasedPublishedPayloadUnreachable()
    {
        var (state, payload) = ReleasePublishedPayload();

        Assert.Empty(state.FamilyData[Family]);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            CollectGarbage();
        }

        Assert.False(IsAlive(payload));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (RuntimeState State, WeakReference<byte[]> Payload) ReleasePublishedPayload()
    {
        var state = NewState();
        var payload = new byte[64 * 1024];
        state.FamilyData[Family] = state.FamilyData[Family].SetItem(
            Key(1),
            new CellState(payload, 1, null));
        var weakPayload = new WeakReference<byte[]>(payload);

        state.ReleaseFlushedGeneration(Flush([Mutation(1, 1)]));
        return (state, weakPayload);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void CollectGarbage()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool IsAlive(WeakReference<byte[]> payload) => payload.TryGetTarget(out _);

    static RuntimeState NewState() =>
        new(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());

    static WalMutation Mutation(int key, ulong sequence) =>
        new(Family.Id, WalOperation.Put, Key(key), [1], sequence, null, null);

    static FrozenMemtableFlush Flush(IReadOnlyList<WalMutation> operations) => new(
        1,
        Family,
        Family.Id,
        operations,
        1,
        operations.Count == 0 ? 0 : operations[^1].Sequence,
        0);

    static byte[] Key(int index) => [(byte)index];
}
