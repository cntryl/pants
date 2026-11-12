using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class WalRuntimeOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldLeaveRuntimeSequenceUnchangedUntilTheOwnerPublishesAWalReceipt(bool grouped)
    {
        using var directory = new TemporaryDirectory();
        var telemetry = new RuntimeTelemetry();
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), telemetry);
        using var barrier = new FlushPipelineFailpointHandler(Failpoint.BeforeWalFlush);
        using var store = LocalDiskStore.Open(directory.Path, state, failpoints: barrier);
        await using var service = new WalRuntimeService(4, store, barrier, new WalMetricsRecorder(telemetry));
        using var first = CreatePayload(state, 1);
        using var second = CreatePayload(state, 2);
        var group = grouped
            ? service.AppendCommitGroupAsync(
                [new WalCommitGroupEntry(first, 3), new WalCommitGroupEntry(second, 6)],
                state.Sequence, PantsDurability.Sync, Failpoint.BeforeCoalescedWalDurabilityBoundary).AsTask()
            : null;
        var single = grouped ? null : service.AppendCommitAsync(first, state.Sequence, PantsDurability.Sync).AsTask();
        var append = (Task?)group ?? single!;
        await barrier.WaitUntilEnteredAsync(TestTimeouts.Expected);
        try
        {
            Assert.False(append.IsCompleted);
            Assert.Equal(0UL, state.Sequence);
            Assert.Equal(PantsEngineHealth.Healthy, state.Health);
            Assert.Equal(0, state.ActiveMemtableBytes.Values.Sum());
        }
        finally
        {
            barrier.Release();
        }

        await append;
        Assert.Equal(0UL, state.Sequence);
        Assert.Empty(state.FamilyData.Single().Value);
        state.Sequence = grouped ? (await group!).CommitSequence : (await single!).CommitSequence;
        Assert.Equal(grouped ? 6UL : 3UL, state.Sequence);
    }

    static CommitPayload CreatePayload(RuntimeState state, long id)
    {
        var family = state.FamilyData.Keys.Single();
        return new CommitPayload(
            id, PantsTransactionMode.ReadWrite, PantsConflictPolicy.LastWriteWins, DateTimeOffset.UnixEpoch,
            state.CreateVersion(), new TransactionOperationSource(null,
            [new TransactionIntentOperation(0, CommitOperationKind.Put, family,
                TestBytes.FromString($"key-{id}"), null, "value"u8.ToArray(), null, null, false)],
                1, DateTimeOffset.UnixEpoch), []);
    }
}
