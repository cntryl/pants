using System.Buffers.Binary;
using System.Collections.Immutable;
using Cntryl.Pants.Exceptions;
using Cntryl.Pants.Runtime.Internal;
using Cntryl.Pants.Scan;
using Cntryl.Pants.Storage;
using Cntryl.Pants.Storage.Internal.Accounting;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Checkpoint;

static class CheckpointCellRunner
{
    static int _executed;

    public static async Task<CheckpointEvidence.Attempt> RunAsync(string campaignId,
        CheckpointCells.Selection selection, string directory, string databasePath)
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {
            throw new InvalidOperationException("A registered fixed cell requires one fresh process per invocation.");
        }
        return await ExecuteAsync(campaignId, selection, directory, databasePath, false, RuntimeDependencies.Default)
            .ConfigureAwait(false);
    }

    internal static Task<CheckpointEvidence.Attempt> RunDiagnosticAsync(string directory, string databasePath,
        int cycles = 4, int rows = 8, RuntimeDependencies? dependencies = null) =>
        ExecuteAsync("diagnostic", new CheckpointCells.Selection(new CheckpointCells.Cell("diagnostic", "diagnostic", cycles, rows, 1), 1),
            directory, databasePath, true, dependencies ?? RuntimeDependencies.Default);

    static async Task<CheckpointEvidence.Attempt> ExecuteAsync(string campaignId,
        CheckpointCells.Selection selection, string directory, string databasePath, bool diagnostic,
        RuntimeDependencies dependencies)
    {
        var time = dependencies.RuntimeTimeProvider;
        var started = time.GetTimestamp();
        var deadline = CheckpointClock.Deadline(time, started, CheckpointCells.CellBudget);
        var attempt = new CheckpointEvidence.Attempt(campaignId, selection, diagnostic)
        {
            CellStarted = started,
            CellDeadline = deadline,
            DatabasePath = databasePath
        };
        var cell = selection.Cell;
        IPantsDatabase? original = null;
        IPantsDatabase? reopened = null;
        MetadataAccountingHandle? originalHandle = null;
        MetadataAccountingHandle? reopenedHandle = null;
        try
        {
            attempt.LoadedBuild = CheckpointProvenance.ObserveLoadedBuild();
            if (!diagnostic && attempt.LoadedBuild.Any(static file => file.Configuration != "Release" || file.OptimizerDisabled))
            {
                throw new InvalidOperationException("Fixed checkpoint measurement requires actual optimized Release assemblies.");
            }
            if (Directory.Exists(databasePath))
            {
                throw new InvalidOperationException("A checkpoint cell requires a fresh, retained database path.");
            }
            var options = PantsOpenOptions.Local(databasePath)
                .WithMemoryBudget(PantsMemoryBudget.FromBytes(512L * 1024 * 1024))
                .WithTransactionMemoryPool(32L * 1024 * 1024)
                .WithMemtableLimits(8L * 1024 * 1024)
                .WithBackgroundCompaction(true);
            var resolved = RuntimePlan.Resolve(options);
            await CheckpointEvidence.WriteAsync(Path.Combine(directory, "resolved-options.json"), new
            {
                resolved.MemoryBudgetBytes,
                resolved.MemtableSizeLimitBytes,
                resolved.MemtableFlushThresholdBytes,
                resolved.TransactionMemoryPoolBytes,
                resolved.TargetSstSizeBytes,
                resolved.L0CompactionTrigger,
                resolved.BackgroundCompaction
            }).ConfigureAwait(false);
            original = await PantsDatabase.OpenForTestingAsync(options, dependencies).ConfigureAwait(false);
            originalHandle = ((DatabaseInstance)original).MetadataAccounting;
            var families = new List<IPantsColumnFamily> { original.ColumnFamilies.DefaultFamily };
            for (var index = 1; index < cell.Families; index++)
            {
                families.Add(await original.ColumnFamilies.CreateAsync($"checkpoint-{index:00}").ConfigureAwait(false));
            }
            CheckpointBoundary.Capture? before = null;
            var latencies = ImmutableArray.CreateBuilder<ulong>(cell.MeasuredCycles);
            for (var cycle = 0; cycle < cell.Cycles; cycle++)
            {
                RequireBudget(time, deadline);
                if (cycle == cell.WarmupCycles)
                {
                    before = await CaptureAsync(original, originalHandle, time, deadline, attempt.BeforeBoundary).ConfigureAwait(false);
                    attempt.Phase = "measured_ingestion";
                }
                var currentCycle = cycle;
                var family = families[cycle % cell.Families];
                await CheckpointCommitPolicy.CommitAsync(time, deadline, attempt.CommitBackpressure,
                    () => CommitCycleAsync(original, family, cell, currentCycle),
                    budget => original.Maintenance.WaitForWriteStallClearAsync(family, budget)).ConfigureAwait(false);
                attempt.AcknowledgedRows += checked((ulong)cell.Rows);
                attempt.Progress += checked((ulong)cell.Rows);
                if (before is not null)
                {
                    attempt.MeasuredAcknowledgedRows += checked((ulong)cell.Rows);
                }
                attempt.FlushInFlight = true;
                await SaveAsync(directory, attempt).ConfigureAwait(false);
                var flushStarted = time.GetTimestamp();
                await original.Maintenance.FlushAsync(family).ConfigureAwait(false);
                if (before is not null)
                {
                    latencies.Add(CheckpointClock.Nanoseconds(time.GetElapsedTime(flushStarted)));
                }
                attempt.FlushInFlight = false;
                attempt.CompletedCycles++;
                attempt.Progress++;
            }
            var after = await CaptureAsync(original, originalHandle, time, deadline, attempt.AfterBoundary).ConfigureAwait(false);
            var elapsed = CheckpointClock.Nanoseconds(time.GetElapsedTime(before!.QueryStarted));
            var delta = after.Snapshot.Delta(before.Snapshot);
            var compactions = checked((ulong)(after.Runtime.CompactionsRun - before.Runtime.CompactionsRun));
            var provisional = CheckpointGate.Evaluate(delta, checked((ulong)cell.MeasuredCycles), compactions);
            attempt.MeasuredWindow = new CheckpointEvidence.Window(before.Snapshot, after.Snapshot, delta,
                before.Runtime, after.Runtime, elapsed, attempt.MeasuredAcknowledgedRows, compactions,
                latencies.MoveToImmutable(), provisional);
            attempt.FinalGate = provisional;
            attempt.Phase = "verification";
            await SaveAsync(directory, attempt).ConfigureAwait(false);
            var lastProgressSave = time.GetTimestamp();
            async ValueTask VerifiedProgressAsync(ulong count)
            {
                attempt.Progress += count;
                if (time.GetElapsedTime(lastProgressSave) >= TimeSpan.FromSeconds(1))
                {
                    await SaveAsync(directory, attempt).ConfigureAwait(false);
                    lastProgressSave = time.GetTimestamp();
                }
            }
            await VerifyAsync(original, cell, time, deadline, VerifiedProgressAsync).ConfigureAwait(false);
            attempt.Verified = true;
            await original.ShutdownAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await original.DisposeAsync().ConfigureAwait(false);
            original = null;
            attempt.OriginalAfterShutdown = originalHandle.Snapshot();
            RequireBudget(time, deadline);
            reopened = await PantsDatabase.OpenForTestingAsync(options, dependencies).ConfigureAwait(false);
            reopenedHandle = ((DatabaseInstance)reopened).MetadataAccounting;
            await VerifyAsync(reopened, cell, time, deadline, VerifiedProgressAsync).ConfigureAwait(false);
            attempt.ReopenedVerified = true;
            var reopenedOwner = reopenedHandle.Snapshot().OwnerId;
            await reopened.ShutdownAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await reopened.DisposeAsync().ConfigureAwait(false);
            reopened = null;
            attempt.ReopenedFinal = reopenedHandle.Snapshot();
            attempt.OriginalFinal = originalHandle.Snapshot();
            _ = attempt.OriginalFinal.Delta(after.Snapshot);
            attempt.FinalGate = CheckpointGate.Settle(provisional, delta.OwnerId, attempt.OriginalFinal,
                reopenedOwner, attempt.ReopenedFinal);
            attempt.Passed = !diagnostic && attempt.FinalGate.Valid;
        }
        catch (Exception exception)
        {
            attempt.TerminalError = exception.ToString();
            attempt.FailureKind = exception.GetType().Name;
        }
        finally
        {
            var errors = ImmutableArray.CreateBuilder<string>();
            foreach (var engine in new[] { reopened, original })
            {
                if (engine is null)
                {
                    continue;
                }
                try
                {
                    await engine.ShutdownAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    errors.Add(exception.ToString());
                }
                try
                {
                    await engine.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    errors.Add(exception.ToString());
                }
            }
            attempt.CleanupErrors = errors.ToImmutable();
            if (attempt.CleanupErrors.Length > 0)
            {
                attempt.Passed = false;
            }
            if (originalHandle is not null)
            {
                attempt.OriginalFinal = originalHandle.Snapshot();
            }
            if (reopenedHandle is not null)
            {
                attempt.ReopenedFinal = reopenedHandle.Snapshot();
            }
            if (attempt.MeasuredWindow is { } window && attempt.OriginalFinal is { } settledOriginal &&
                attempt.ReopenedFinal is { } settledReopened)
            {
                attempt.FinalGate = CheckpointGate.Settle(window.ProvisionalGate, window.Delta.OwnerId,
                    settledOriginal, settledReopened.OwnerId, settledReopened);
            }
            attempt.Passed = !diagnostic && attempt.FinalGate?.Valid == true && attempt.TerminalError is null &&
                             attempt.CleanupErrors.IsEmpty && attempt.Verified && attempt.ReopenedVerified && !attempt.FlushInFlight;
            attempt.Phase = "complete";
            await SaveAsync(directory, attempt).ConfigureAwait(false);
        }
        return attempt;
    }

    static Task SaveAsync(string directory, CheckpointEvidence.Attempt attempt) =>
        CheckpointEvidence.WriteAsync(Path.Combine(directory, "attempt.json"), attempt);

    static async ValueTask<CheckpointBoundary.Capture> CaptureAsync(IPantsDatabase engine,
        MetadataAccountingHandle handle, TimeProvider time, long deadline, CheckpointBoundary.Observation observation) =>
        await CheckpointBoundary.CaptureAsync(time, deadline, TimeSpan.FromSeconds(30), observation, async remaining =>
        {
            using var cancellation = new CancellationTokenSource(remaining, time);
            var runtime = await engine.Diagnostics.GetRuntimeMetricsAsync(cancellation.Token).ConfigureAwait(false);
            return (runtime, handle.Snapshot());
        }).ConfigureAwait(false);

    static async ValueTask CommitCycleAsync(IPantsDatabase engine, IPantsColumnFamily family,
        CheckpointCells.Cell cell, int cycle)
    {
        await using var transaction = await engine.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite).ConfigureAwait(false);
        for (var index = 0; index < cell.Rows; index++)
        {
            var (key, value) = Record(cycle % cell.Families, cycle * cell.Rows + index);
            transaction.Put(key, value);
        }
        await transaction.CommitAsync(PantsWriteOptions.Sync).ConfigureAwait(false);
    }

    static async Task VerifyAsync(IPantsDatabase engine, CheckpointCells.Cell cell, TimeProvider time, long deadline,
        Func<ulong, ValueTask> verifiedProgress)
    {
        for (var familyIndex = 0; familyIndex < cell.Families; familyIndex++)
        {
            RequireBudget(time, deadline);
            var family = familyIndex == 0 ? engine.ColumnFamilies.DefaultFamily :
                await engine.ColumnFamilies.GetAsync($"checkpoint-{familyIndex:00}").ConfigureAwait(false)
                ?? throw new InvalidOperationException("Acknowledged family is missing.");
            await using var read = await engine.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly).ConfigureAwait(false);
            var expectedCount = 0;
            for (var cycle = familyIndex; cycle < cell.Cycles; cycle += cell.Families)
            {
                RequireBudget(time, deadline);
                for (var row = 0; row < cell.Rows; row++)
                {
                    var (key, expected) = Record(familyIndex, cycle * cell.Rows + row);
                    var actual = await read.GetAsync(key).ConfigureAwait(false);
                    if (!actual.HasValue || !actual.Value.Span.SequenceEqual(expected))
                    {
                        throw new InvalidOperationException("Acknowledged point key/value differs.");
                    }
                    expectedCount++;
                    if (expectedCount % 64 == 0)
                    {
                        await verifiedProgress(64).ConfigureAwait(false);
                    }
                }
            }
            await using var scan = await read.ScanAsync(new PantsScanQuery()).ConfigureAwait(false);
            var seen = 0;
            await foreach (var item in ((IAsyncEnumerable<PantsEntry>)scan).ConfigureAwait(false))
            {
                RequireBudget(time, deadline);
                var cycle = familyIndex + seen / cell.Rows * cell.Families;
                var (key, value) = Record(familyIndex, cycle * cell.Rows + seen % cell.Rows);
                if (seen >= expectedCount || !item.Key.Span.SequenceEqual(key) || !item.Value.Span.SequenceEqual(value))
                {
                    throw new InvalidOperationException("Full scan key/order/value differs.");
                }
                seen++;
                if (seen % 64 == 0)
                {
                    await verifiedProgress(64).ConfigureAwait(false);
                }
            }
            if (seen != expectedCount)
            {
                throw new InvalidOperationException("Full scan count differs from acknowledged rows.");
            }
        }
    }

    internal static (byte[] Key, byte[] Value) Record(int family, int ordinal)
    {
        var key = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(key, checked((ulong)family));
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(8), checked((ulong)ordinal));
        var state = unchecked(((ulong)ordinal + 1) * 0x9e3779b97f4a7c15UL ^ (ulong)family * 0xbf58476d1ce4e5b9UL);
        var value = new byte[CheckpointCells.ValueBytes];
        for (var offset = 0; offset < value.Length; offset += 8)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(offset), unchecked(state * 0x2545f4914f6cdd1dUL));
        }
        return (key, value);
    }

    static void RequireBudget(TimeProvider time, long deadline)
    {
        if (CheckpointClock.Remaining(time, deadline) <= TimeSpan.Zero)
        {
            throw new PantsTimeoutException("The original fixed-cell deadline expired.");
        }
    }
}
