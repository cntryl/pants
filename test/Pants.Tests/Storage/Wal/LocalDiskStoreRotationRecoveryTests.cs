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
    public async Task ShouldFenceWalAfterSealedSegmentGetsReplacementWriter()
    {
        using var directory = new TemporaryDirectory();
        var telemetry = new RuntimeTelemetry();
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            telemetry);
        var failpoints = new ArmableFailpointHandler();
        using (var store = LocalDiskStore.Open(directory.Path, state, failpoints: failpoints))
        {
            _ = store.AppendCommit(
                CreateCommitPayload(state),
                state,
                PantsDurability.Sync);

            failpoints.Arm(Failpoint.AfterWalRotation);
            _ = Assert.Throws<IOException>(() => store.RotateActiveLocalWal());

            Assert.True(store.IsWalFenced);
            Assert.Throws<PantsFencedException>(() => store.AppendCommit(
                CreateCommitPayload(state),
                state,
                PantsDurability.Sync));
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "rotation-value",
            TestBytes.ToText((await reader.GetAsync("rotation-key"u8.ToArray()))!.Value));
    }

    [Theory]
    [InlineData(nameof(Failpoint.AfterWalSealRename))]
    [InlineData(nameof(Failpoint.BeforeWalSealDirectorySync))]
    [InlineData(nameof(Failpoint.BeforeWalReplacementWriterCreate))]
    [InlineData(nameof(Failpoint.AfterWalReplacementWriterCreate))]
    [InlineData(nameof(Failpoint.BeforeWalReplacementDirectorySync))]
    [InlineData(nameof(Failpoint.AfterWalReplacementDirectorySync))]
    public async Task ShouldFenceAtEveryPostRenameWalRotationBoundary(string boundaryName)
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            new RuntimeTelemetry());
        var failpoints = new ArmableFailpointHandler();
        using (var store = LocalDiskStore.Open(directory.Path, state, failpoints: failpoints))
        {
            _ = store.AppendCommit(
                CreateCommitPayload(state),
                state,
                PantsDurability.Sync);

            failpoints.Arm(Enum.Parse<Failpoint>(boundaryName));
            _ = Assert.Throws<IOException>(() => store.RotateActiveLocalWal());

            Assert.True(store.IsWalFenced);
            Assert.Equal(PantsEngineHealth.Degraded, store.GetHealth(state));
            Assert.Throws<PantsFencedException>(() => store.AppendCommit(
                CreateCommitPayload(state),
                state,
                PantsDurability.Sync));
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "rotation-value",
            TestBytes.ToText((await reader.GetAsync("rotation-key"u8.ToArray()))!.Value));
    }

    [Fact]
    public void ShouldReuseDurableSegmentReservationWhenRotationFailsBeforeRename()
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            new RuntimeTelemetry());
        var failpoints = new ArmableFailpointHandler();
        using (var store = LocalDiskStore.Open(directory.Path, state, failpoints: failpoints))
        {
            _ = store.AppendCommit(CreateCommitPayload(state), state, PantsDurability.Sync);

            failpoints.Arm(Failpoint.AfterWalRotationStreamDisposed);
            _ = Assert.Throws<IOException>(() => store.RotateActiveLocalWal());

            Assert.False(store.IsWalFenced);
            _ = store.AppendCommit(CreateCommitPayload(state), state, PantsDurability.Sync);
            _ = store.RotateActiveLocalWal();

            Assert.True(File.Exists(Path.Combine(
                directory.Path,
                "wal",
                $"{1UL:00000000000000000000}.wal")));
        }

        var recoveredState = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            new RuntimeTelemetry());
        using var reopened = LocalDiskStore.Open(directory.Path, recoveredState);
        _ = reopened.AppendCommit(
            CreateCommitPayload(recoveredState),
            recoveredState,
            PantsDurability.Sync);
        _ = reopened.RotateActiveLocalWal();

        Assert.True(File.Exists(Path.Combine(
            directory.Path,
            "wal",
            $"{2UL:00000000000000000000}.wal")));
    }

    [Fact]
    public void ShouldRecoverReservedWalIdentityAcrossCrashBeforeRename()
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            new RuntimeTelemetry());
        var failpoints = new ArmableFailpointHandler();
        using (var store = LocalDiskStore.Open(directory.Path, state, failpoints: failpoints))
        {
            _ = store.AppendCommit(CreateCommitPayload(state), state, PantsDurability.Sync);
            failpoints.Arm(Failpoint.AfterWalRotationStreamDisposed);

            _ = Assert.Throws<IOException>(() => store.RotateActiveLocalWal());

            Assert.True(File.Exists(Path.Combine(directory.Path, "wal", "wal.log")));
            Assert.False(File.Exists(Path.Combine(
                directory.Path,
                "wal",
                $"{1UL:00000000000000000000}.wal")));
        }

        var recoveredState = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            new RuntimeTelemetry());
        using var reopened = LocalDiskStore.Open(directory.Path, recoveredState);

        Assert.Equal(state.Sequence, recoveredState.Sequence);
        Assert.True(File.Exists(Path.Combine(
            directory.Path,
            "wal",
            $"{1UL:00000000000000000000}.wal")));
        _ = reopened.AppendCommit(
            CreateCommitPayload(recoveredState),
            recoveredState,
            PantsDurability.Sync);
        Assert.True(
            recoveredState.Sequence > state.Sequence,
            "The next commit must allocate beyond the durable commit-sequence reservation.");
        _ = reopened.RotateActiveLocalWal();

        Assert.True(File.Exists(Path.Combine(
            directory.Path,
            "wal",
            $"{2UL:00000000000000000000}.wal")));
    }

    [Fact]
    public void ShouldRejectMissingUnretiredWalSegmentInsteadOfSkippingItsHistory()
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            new RuntimeTelemetry());
        using (var store = LocalDiskStore.Open(directory.Path, state))
        {
            _ = store.AppendCommit(
                CreateCommitPayload(state, "first-segment"u8.ToArray(), "first-value"u8.ToArray()),
                state,
                PantsDurability.Sync);
            _ = store.RotateActiveLocalWal();
            _ = store.AppendCommit(
                CreateCommitPayload(state, "second-segment"u8.ToArray(), "second-value"u8.ToArray()),
                state,
                PantsDurability.Sync);
            _ = store.RotateActiveLocalWal();
        }

        File.Delete(Path.Combine(directory.Path, "wal", $"{1UL:00000000000000000000}.wal"));
        var recoveredState = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            new RuntimeTelemetry());

        var exception = Assert.Throws<PantsCorruptionException>(
            () => LocalDiskStore.Open(directory.Path, recoveredState));

        Assert.Equal(PantsErrorCode.Corruption, exception.Code);
    }

    [Fact]
    public async Task ShouldReportPersistenceAnomalyWhenWalPruneDirectorySyncFails()
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            new RuntimeTelemetry());
        var failpoints = new ArmableFailpointHandler();
        using (var store = LocalDiskStore.Open(directory.Path, state, failpoints: failpoints))
        {
            _ = store.AppendCommit(CreateCommitPayload(state), state, PantsDurability.Sync);
            _ = store.RotateActiveLocalWal();
            failpoints.Arm(Failpoint.BeforeWalPruneDirectorySync);

            store.Flush(state);

            Assert.False(store.IsWalFenced);
            Assert.Equal(PantsEngineHealth.Degraded, store.GetHealth(state));
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "rotation-value",
            TestBytes.ToText((await reader.GetAsync("rotation-key"u8.ToArray()))!.Value));
    }

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

            var fenced = Assert.Throws<PantsFencedException>(() =>
                store.CreateColumnFamily(new ColumnFamilyIdentity(1, "fenced", 1)));
            Assert.Equal(PantsErrorCode.Fenced, fenced.Code);
            Assert.Same(failure, fenced.InnerException);
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
    public void ShouldKeepCloudPendingWritesGivenBufferedSyncRunsBeforeSealedSegmentIsAdmitted()
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            new RuntimeTelemetry());
        using var store = LocalDiskStore.Open(directory.Path, state);
        _ = store.AppendCommit(
            CreateCommitPayload(state),
            state,
            PantsDurability.Buffered);
        var segment = Assert.IsType<SealedWalSegment>(store.SealActiveWalForCloud());

        // The background Buffered sync can fire at any point after the append; once the segment
        // is sealed it must not count the empty replacement WAL's fsync as admitting it.
        store.SyncBufferedWalIfPending();

        Assert.Equal(1, store.WalPendingWrites);
        store.CompleteCloudWalSeal(segment);
        Assert.Equal(0, store.WalPendingWrites);
    }

    [Fact]
    public void ShouldPageSealedCloudWalWithinCountAndByteBoundsAndKeepItsInitialBoundary()
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            new RuntimeTelemetry());
        using var store = LocalDiskStore.Open(directory.Path, state);
        var createdSegments = new List<SealedWalSegment>();
        for (var index = 0; index < 4; index++)
        {
            _ = store.AppendCommit(
                CreateCommitPayload(
                    state,
                    TestBytes.FromString($"backlog-key-{index}"),
                    TestBytes.FromString($"backlog-value-{index}"),
                    checked((ulong)(index + 1))),
                state,
                PantsDurability.Buffered);
            var segment = Assert.IsType<SealedWalSegment>(store.SealActiveWalForCloud());
            store.CompleteCloudWalSeal(segment);
            createdSegments.Add(segment);
        }

        var countBounded = store.GetSealedWalSegmentsForCloudPublication(
            maximumSegments: 2,
            maximumBytes: long.MaxValue);

        Assert.Equal([1UL, 2UL], countBounded.Segments.Select(static segment => segment.SegmentId));
        Assert.Equal(4UL, countBounded.ThroughSegmentId);
        Assert.True(countBounded.Segments.Count <= 2);

        foreach (var segment in countBounded.Segments)
        {
            store.DeleteCloudDurableWalSegment(segment);
        }

        _ = store.AppendCommit(
            CreateCommitPayload(
                state,
                TestBytes.FromString("backlog-key-4"),
                TestBytes.FromString("backlog-value-4"),
                5),
            state,
            PantsDurability.Buffered);
        var lateSegment = Assert.IsType<SealedWalSegment>(store.SealActiveWalForCloud());
        store.CompleteCloudWalSeal(lateSegment);
        createdSegments.Add(lateSegment);

        var byteLimit = Math.Min(
            createdSegments[2].Bytes.Length,
            createdSegments[3].Bytes.Length);
        var byteBounded = store.GetSealedWalSegmentsForCloudPublication(
            countBounded.ThroughSegmentId,
            maximumSegments: 16,
            maximumBytes: byteLimit);

        Assert.Single(byteBounded.Segments);
        Assert.Equal(3UL, byteBounded.Segments[0].SegmentId);
        Assert.True(byteBounded.Segments.Sum(static segment => segment.Bytes.Length) <= byteLimit ||
                    byteBounded.Segments[0].Bytes.Length > byteLimit);
        Assert.Equal(countBounded.ThroughSegmentId, byteBounded.ThroughSegmentId);

        var oversized = store.GetSealedWalSegmentsForCloudPublication(
            countBounded.ThroughSegmentId,
            maximumSegments: 16,
            maximumBytes: 1);

        Assert.Single(oversized.Segments);
        Assert.Equal(3UL, oversized.Segments[0].SegmentId);
        Assert.True(oversized.Segments[0].Bytes.Length > 1);

        store.DeleteCloudDurableWalSegment(oversized.Segments[0]);
        var finalInSnapshot = store.GetSealedWalSegmentsForCloudPublication(
            countBounded.ThroughSegmentId,
            maximumSegments: 16,
            maximumBytes: long.MaxValue);

        Assert.Equal([4UL], finalInSnapshot.Segments.Select(static segment => segment.SegmentId));
        store.DeleteCloudDurableWalSegment(finalInSnapshot.Segments[0]);

        var completedSnapshot = store.GetSealedWalSegmentsForCloudPublication(
            countBounded.ThroughSegmentId,
            maximumSegments: 16,
            maximumBytes: long.MaxValue);
        Assert.Empty(completedSnapshot.Segments);

        var laterBacklog = store.GetSealedWalSegmentsForCloudPublication(
            maximumSegments: 16,
            maximumBytes: long.MaxValue);
        Assert.Equal([5UL], laterBacklog.Segments.Select(static segment => segment.SegmentId));
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

    static CommitPayload CreateCommitPayload(RuntimeState state) =>
        CreateCommitPayload(state, "rotation-key"u8.ToArray(), "rotation-value"u8.ToArray());

    static CommitPayload CreateCommitPayload(
        RuntimeState state,
        byte[] key,
        byte[] value,
        ulong transactionId = 1)
    {
        var operation = new TransactionIntentOperation(
            0,
            CommitOperationKind.Put,
            DefaultFamily,
            key,
            null,
            value,
            null,
            null,
            false);
        var source = new TransactionOperationSource(
            null,
            [operation],
            1,
            DateTimeOffset.UnixEpoch);
        return new CommitPayload(
            checked((long)transactionId),
            PantsTransactionMode.ReadWrite,
            PantsConflictPolicy.LastWriteWins,
            DateTimeOffset.UnixEpoch,
            state.CreateVersion(),
            source,
            []);
    }
}
