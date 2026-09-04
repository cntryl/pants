using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class LocalDiskStoreRotationRecoveryTests
{
    static readonly ColumnFamilyIdentity DefaultFamily = new(
        0,
        "default",
        RuntimeState.DefaultFamilyVersion);

    [Fact]
    public async Task ShouldFenceLayoutAndRecoverDurableCommitGivenRotationReopenFails()
    {
        using var directory = new TemporaryDirectory();
        var telemetry = new RuntimeTelemetry();
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            telemetry);
        var failpoints = new WalRotationRecoveryFailureFailpointHandler();
        using (var store = LocalDiskStore.Open(
                   directory.Path,
                   state,
                   failpoints: failpoints))
        {
            var result = store.AppendCommit(
                CreateCommitPayload(state),
                state,
                PantsDurability.Sync);
            Assert.Null(result.PostDurabilityFailure);

            var failure = Assert.Throws<WalRotationRecoveryException>(() => store.RotateActiveLocalWal());
            Assert.IsType<IOException>(failure.RotationFailure);
            Assert.IsType<IOException>(failure.RecoveryFailure);

            var aborted = Assert.Throws<PantsAbortedException>(() =>
                store.CreateColumnFamily(new ColumnFamilyIdentity(1, "fenced", 1)));
            Assert.Same(failure, aborted.InnerException);
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await reader.GetAsync("rotation-key"u8.ToArray());
        Assert.Equal(
            "rotation-value",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(value)));
    }

    [Fact]
    public void ShouldClearCloudPendingWritesOnlyAfterSealedSegmentIsAdmitted()
    {
        using var directory = new TemporaryDirectory();
        var telemetry = new RuntimeTelemetry();
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            telemetry);
        var failpoints = new OneShotCloudWalSealFailureHandler();
        using var store = LocalDiskStore.Open(
            directory.Path,
            state,
            failpoints: failpoints);
        _ = store.AppendCommit(
            CreateCommitPayload(state),
            state,
            PantsDurability.Buffered);

        Assert.Equal(1, store.WalPendingWrites);
        _ = Assert.Throws<IOException>(() => store.SealActiveWalForCloud());
        Assert.Equal(1, store.WalPendingWrites);

        var segment = Assert.IsType<SealedWalSegment>(store.SealActiveWalForCloud());
        Assert.Equal(1, store.WalPendingWrites);
        store.CompleteCloudWalSeal(segment);
        Assert.Equal(0, store.WalPendingWrites);
    }

    [Fact]
    public void ShouldFenceCloudWalRotationGivenLeaseIsLostAfterFlush()
    {
        using var directory = new TemporaryDirectory();
        var telemetry = new RuntimeTelemetry();
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            telemetry);
        var failpoints = new CloudWalSealLeaseLossFailpointHandler(directory.Path);
        using var store = LocalDiskStore.Open(
            directory.Path,
            state,
            failpoints: failpoints);
        _ = store.AppendCommit(
            CreateCommitPayload(state),
            state,
            PantsDurability.Buffered);

        _ = Assert.Throws<PantsFencedException>(() => store.SealActiveWalForCloud());

        Assert.False(store.IsLeaseHealthy);
        Assert.Equal(1, store.WalPendingWrites);
        Assert.NotEqual(0, store.ActiveWalBytes);
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "wal"),
            "*.wal",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ShouldRevalidateProviderAuthorityAfterCloudWalSealFlush()
    {
        using var directory = new TemporaryDirectory();
        var telemetry = new RuntimeTelemetry();
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            telemetry);
        using var store = LocalDiskStore.Open(directory.Path, state);
        _ = store.AppendCommit(
            CreateCommitPayload(state),
            state,
            PantsDurability.Buffered);
        var validations = 0;
        await using var runtime = new WalRuntimeService(
            8,
            store,
            NullPantsFailpointHandler.Instance,
            new WalMetricsRecorder(telemetry),
            () =>
            {
                Interlocked.Increment(ref validations);
                throw new PantsFencedException("Injected provider lease loss.");
            });

        _ = await Assert.ThrowsAsync<PantsFencedException>(() => runtime.SealCloudWalAsync().AsTask());

        Assert.Equal(1, Volatile.Read(ref validations));
        Assert.Equal(1, store.WalPendingWrites);
        Assert.NotEqual(0, store.ActiveWalBytes);
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "wal"),
            "*.wal",
            SearchOption.TopDirectoryOnly));
    }

    static CommitPayload CreateCommitPayload(RuntimeState state)
    {
        var operation = new TransactionIntentOperation(
            0,
            CommitOperationKind.Put,
            DefaultFamily,
            "rotation-key"u8.ToArray(),
            null,
            "rotation-value"u8.ToArray(),
            null,
            null,
            false);
        var source = new TransactionOperationSource(
            null,
            [operation],
            1,
            DateTimeOffset.UnixEpoch);
        return new CommitPayload(
            1,
            PantsTransactionMode.ReadWrite,
            PantsConflictPolicy.LastWriteWins,
            DateTimeOffset.UnixEpoch,
            state.CreateVersion(),
            source,
            []);
    }
}
