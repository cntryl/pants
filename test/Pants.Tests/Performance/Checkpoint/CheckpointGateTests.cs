using Cntryl.Pants.Checkpoint;
using Cntryl.Pants.Storage.Internal.Accounting;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Performance.Checkpoint;

public sealed class CheckpointGateTests
{
    [Fact]
    public void ShouldUseExactOrdinaryPersistentRatiosAndInclusiveFiveMillisecondP95Boundary()
    {
        // Constructed policy control; these counters are not native I/O evidence.
        var snapshot = PolicySnapshot();

        var gate = CheckpointGate.Evaluate(snapshot, 1, 1);

        Assert.True(gate.Valid);
        Assert.True(gate.SnapshotMiss);
        Assert.True(gate.CheckpointMiss);
        Assert.True(gate.Miss);
        Assert.Equal(5_000_000UL, gate.CheckpointP95LowerNanoseconds);
    }

    [Fact]
    public void ShouldInvalidateForcedPersistentFailureWithoutAddingItToMeasuredRatios()
    {
        var snapshot = PolicySnapshot();
        var forced = snapshot.Bucket(MetadataOrigin.Administration, MetadataMedium.Persistent);
        var counters = forced.Counters.ToBuilder();
        counters[(int)MetadataCounter.OperationAttempts] = 1;
        counters[(int)MetadataCounter.OperationFailures] = 1;
        snapshot = snapshot with { Buckets = snapshot.Buckets.SetItem(12, forced with { Counters = counters.ToImmutable() }) };

        var gate = CheckpointGate.Evaluate(snapshot, 1, 1);

        Assert.False(gate.Valid);
        Assert.Null(gate.Miss);
        Assert.True(gate.SnapshotMiss);
        Assert.Contains(gate.InvalidReasons, static reason => reason.Contains("persistent metadata failed", StringComparison.Ordinal));
    }

    [Fact]
    public void ShouldWithdrawProvisionalGateForLateOriginalOwnerEvidenceOrReusedReopenedOwner()
    {
        var measured = PolicySnapshot();
        var gate = CheckpointGate.Evaluate(measured, 1, 1);
        var reopened = new MetadataAccountingOwner().Handle.Snapshot();

        var late = CheckpointGate.Settle(gate, measured.OwnerId, measured with { LateMutations = 1 }, reopened.OwnerId, reopened);
        var reused = CheckpointGate.Settle(gate, measured.OwnerId, measured, measured.OwnerId, measured);

        Assert.False(late.Valid);
        Assert.Null(late.Miss);
        Assert.False(reused.Valid);
        Assert.Null(reused.Miss);
    }

    [Fact]
    public async Task ShouldInvalidateRealDurableSnapshotWhosePersistentCheckpointDidNotComplete()
    {
        using var directory = new TemporaryDirectory();
        var failure = new ArmableFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false), new RuntimeDependencies(failure));
        var handle = ((DatabaseInstance)database).MetadataAccounting;
        await using (var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
        {
            transaction.Put("row"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }
        var before = handle.Snapshot();
        failure.Arm(Failpoint.AfterManifestSnapshotDurable);

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var gate = CheckpointGate.Evaluate(handle.Snapshot().Delta(before), 1, 1);

        Assert.False(gate.Valid);
        Assert.Null(gate.Miss);
        Assert.Contains(gate.InvalidReasons, static reason => reason.Contains("checkpoint durability/completion", StringComparison.Ordinal));
        await database.ShutdownAsync(TestTimeouts.Expected);
        await using var reopened = await PantsDatabase.OpenAsync(database.Options);
        await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("value"u8.ToArray(), (await reader.GetAsync("row"u8.ToArray()))?.ToArray());
    }

    [Fact]
    public async Task ShouldExcludeWarmupFlushAndRetainDistinctSettledOwnersForRealReducedDiagnostic()
    {
        using var directory = new TemporaryDirectory();

        var attempt = await CheckpointCellRunner.RunDiagnosticAsync(Path.Combine(directory.Path, "evidence"),
            Path.Combine(directory.Path, "database"));

        Assert.Null(attempt.TerminalError);
        Assert.True(attempt.Verified);
        Assert.True(attempt.ReopenedVerified);
        Assert.True(attempt.Diagnostic);
        Assert.False(attempt.Passed);
        var window = Assert.IsType<CheckpointEvidence.Window>(attempt.MeasuredWindow);
        Assert.Equal(3UL, window.Delta.Bucket(MetadataOrigin.OrdinaryLocalFlush, MetadataMedium.Persistent)[MetadataCounter.FlushCommittedCount]);
        Assert.Equal(24UL, window.AcknowledgedRows);
        Assert.Equal(36UL, attempt.Progress);
        var captured = await CheckpointEvidence.ReadAsync<CheckpointEvidence.Attempt>(
            Path.Combine(directory.Path, "evidence", "attempt.json"));
        Assert.True(captured.Verified && captured.ReopenedVerified);
        Assert.NotNull(captured.MeasuredWindow);
        Assert.Equal(window.After.OwnerId, captured.MeasuredWindow.After.OwnerId);
        Assert.Equal(3UL, captured.MeasuredWindow.Delta.Bucket(MetadataOrigin.OrdinaryLocalFlush,
            MetadataMedium.Persistent)[MetadataCounter.FlushCommittedCount]);
        Assert.NotNull(attempt.OriginalFinal);
        Assert.NotNull(attempt.ReopenedFinal);
        Assert.NotEqual(attempt.OriginalFinal.OwnerId, attempt.ReopenedFinal.OwnerId);
        Assert.All(attempt.OriginalFinal.Buckets, static bucket => Assert.Equal(0UL, bucket.ActiveOperations));
        Assert.All(attempt.ReopenedFinal.Buckets, static bucket => Assert.Equal(0UL, bucket.ActiveOperations));
    }

    [Fact]
    public async Task ShouldKeepMeasuredRatiosFrozenWhenLaterRealPersistentFailureInvalidatesSettlement()
    {
        using var directory = new TemporaryDirectory();
        var failure = new ArmableFailpointHandler();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2,
                L1TargetSizeBytes: 1, MaximumLevels: 3, BackgroundEnabled: false));
        var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(failure));
        var handle = ((DatabaseInstance)database).MetadataAccounting;
        CheckpointGate.Verdict measured;
        MetadataAccountingSnapshot after;
        try
        {
            var before = handle.Snapshot();
            var runtimeBefore = await database.Diagnostics.GetRuntimeMetricsAsync();
            await PutAndFlushAsync(database);
            await PutAndFlushAsync(database);
            await database.Maintenance.CompactAllAsync();
            var runtimeAfter = await database.Diagnostics.GetRuntimeMetricsAsync();
            after = handle.Snapshot();
            measured = CheckpointGate.Evaluate(after.Delta(before), 2,
                checked((ulong)(runtimeAfter.CompactionsRun - runtimeBefore.CompactionsRun)));
            Assert.True(measured.Valid, string.Join("; ", measured.InvalidReasons));
            failure.Arm(Failpoint.AfterManifestSnapshotDurable);
            await PutAndFlushAsync(database);
            await database.ShutdownAsync(TestTimeouts.Expected);
        }
        finally
        {
            await database.DisposeAsync();
        }
        var original = handle.Snapshot();
        MetadataAccountingHandle reopenedHandle;
        await using (var reopened = await PantsDatabase.OpenAsync(options))
        {
            reopenedHandle = ((DatabaseInstance)reopened).MetadataAccounting;
            await reopened.ShutdownAsync(TestTimeouts.Expected);
        }
        var settledReopened = reopenedHandle.Snapshot();

        var final = CheckpointGate.Settle(measured, after.OwnerId, original,
            settledReopened.OwnerId, settledReopened);

        Assert.False(final.Valid);
        Assert.Null(final.Miss);
        Assert.Equal(measured.SnapshotMiss, final.SnapshotMiss);
        Assert.Equal(measured.CheckpointMiss, final.CheckpointMiss);
        Assert.Equal(measured.CheckpointP95LowerNanoseconds, final.CheckpointP95LowerNanoseconds);
        Assert.Contains(final.InvalidReasons, static reason => reason.Contains("persistent metadata failed", StringComparison.Ordinal));
    }

    static async Task PutAndFlushAsync(IPantsDatabase database)
    {
        await using (var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
        {
            transaction.Put("overlap"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }

    static MetadataAccountingSnapshot PolicySnapshot()
    {
        var snapshot = new MetadataAccountingOwner().Handle.Snapshot();
        var ordinary = snapshot.Buckets[0];
        var counters = ordinary.Counters.ToBuilder();
        counters[(int)MetadataCounter.FlushCommittedCount] = 1;
        counters[(int)MetadataCounter.FlushCommittedSstBytes] = 10_000;
        counters[(int)MetadataCounter.FullPublicationNanoseconds] = 25_000_000;
        counters[(int)MetadataCounter.SnapshotIssuedBytes] = 500;
        counters[(int)MetadataCounter.CheckpointAttempts] = 1;
        counters[(int)MetadataCounter.CheckpointCompleteCount] = 1;
        counters[(int)MetadataCounter.SnapshotDurableCount] = 1;
        counters[(int)MetadataCounter.CheckpointNanoseconds] = 5_000_000;
        ordinary = ordinary with
        {
            Counters = counters.ToImmutable(),
            CheckpointLatency = ordinary.CheckpointLatency.SetItem(20, 1),
            PublicationLatency = ordinary.PublicationLatency.SetItem(64, 1),
            CommittedSstSizes = ordinary.CommittedSstSizes.SetItem(13, 1)
        };
        return snapshot with { Buckets = snapshot.Buckets.SetItem(0, ordinary) };
    }
}
