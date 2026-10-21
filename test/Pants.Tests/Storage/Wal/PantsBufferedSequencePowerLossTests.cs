using System.Globalization;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

[Collection(CrashProcessTestGroup.Name)]
public sealed class PantsBufferedSequencePowerLossTests
{
    const string DatabaseEnvironmentVariable = "PANTS_BUFFERED_SEQUENCE_DATABASE";
    const string ReadyEnvironmentVariable = "PANTS_BUFFERED_SEQUENCE_READY";

    static readonly ColumnFamilyIdentity DefaultFamily = new(
        0,
        "default",
        RuntimeState.DefaultFamilyVersion);

    [Fact]
    public async Task ShouldNotReuseSequencesOfALostUnsyncedTailAfterPowerLoss()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "db");
        var readyPath = Path.Combine(directory.Path, "buffered.ready");
        var start = CrashChildProcess.CreateStartInfo(
            typeof(PantsBufferedSequencePowerLossTests),
            nameof(ShouldLeaveBufferedCommitInUnsyncedTailForParentPowerLoss));
        start.Environment[DatabaseEnvironmentVariable] = databasePath;
        start.Environment[ReadyEnvironmentVariable] = readyPath;

        using var child = await CrashChildProcess.StartAndWaitForReadinessAsync(
            start,
            "buffered sequence reservation child",
            databasePath,
            readyPath);
        try
        {
            var marker = (await File.ReadAllLinesAsync(readyPath))
                .Select(line => long.Parse(line, CultureInfo.InvariantCulture))
                .ToArray();
            Assert.Equal(2, marker.Length);
            var lostSequence = marker[0];
            var syncedWalLength = marker[1];

            child.TryKillProcessTree();
            await child.Process.WaitForExitAsync().WaitAsync(TestTimeouts.Expected);

            var walPath = Path.Combine(databasePath, "wal", "wal.log");
            Assert.True(new FileInfo(walPath).Length > syncedWalLength);
            using var wal = new FileStream(
                walPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.Read);
            wal.SetLength(syncedWalLength);
            wal.Flush(true);

            await WaitForLockReleaseAsync(databasePath);
            await ExpireCrashedLeaseAsync(databasePath);
            await using var recovered = await PantsDatabase.OpenAsync(
                PantsOpenOptions.Local(databasePath).WithBackgroundCompaction(false));

            Assert.Equal("value", await ReadTextAsync(recovered, "synced"));
            Assert.Null(await ReadTextAsync(recovered, "buffered"));

            await CommitSyncAsync(recovered, "after");
            var nextSequence = (await recovered.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence;
            Assert.True(
                nextSequence > lostSequence,
                $"next sequence {nextSequence} reused the lost buffered sequence {lostSequence}");
        }
        finally
        {
            child.TryKillProcessTree();
        }
    }

    [Fact]
    public async Task ShouldKeepRecoveredTailWhenPowerIsLostAfterAProcessCrash()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "db");
        var readyPath = Path.Combine(directory.Path, "buffered.ready");
        var start = CrashChildProcess.CreateStartInfo(
            typeof(PantsBufferedSequencePowerLossTests),
            nameof(ShouldLeaveBufferedCommitInUnsyncedTailForParentPowerLoss));
        start.Environment[DatabaseEnvironmentVariable] = databasePath;
        start.Environment[ReadyEnvironmentVariable] = readyPath;

        using var child = await CrashChildProcess.StartAndWaitForReadinessAsync(
            start,
            "recovered buffered WAL child",
            databasePath,
            readyPath);
        try
        {
            var marker = (await File.ReadAllLinesAsync(readyPath))
                .Select(line => long.Parse(line, CultureInfo.InvariantCulture))
                .ToArray();
            Assert.Equal(2, marker.Length);
            var acceptedSequence = marker[0];
            var priorDurableWalLength = marker[1];

            child.TryKillProcessTree();
            await child.Process.WaitForExitAsync().WaitAsync(TestTimeouts.Expected);
            await WaitForLockReleaseAsync(databasePath);
            await ExpireCrashedLeaseAsync(databasePath);

            var walPath = Path.Combine(databasePath, "wal", "wal.log");
            Assert.True(new FileInfo(walPath).Length > priorDurableWalLength);
            var failedSyncProbe = new RecoveryWalSyncProbe(walPath, throwOnSync: true);
            var syncFailure = Assert.Throws<StorageException>(() => LocalDiskStore.Open(
                databasePath,
                new RuntimeState(
                    new ManualClock(DateTimeOffset.UnixEpoch),
                    new RuntimeTelemetry()),
                failpoints: failedSyncProbe));
            Assert.IsType<IOException>(syncFailure.InnerException);
            Assert.Equal(1, failedSyncProbe.SyncAttempts);

            var syncProbe = new RecoveryWalSyncProbe(walPath);
            byte[] durableWalImage;
            using (LocalDiskStore.Open(
                       databasePath,
                       new RuntimeState(
                           new ManualClock(DateTimeOffset.UnixEpoch),
                           new RuntimeTelemetry()),
                       failpoints: syncProbe))
            {
                Assert.Equal(1, syncProbe.SyncAttempts);
                Assert.True(syncProbe.WalLengthAtSync > priorDurableWalLength);
                durableWalImage = await File.ReadAllBytesAsync(walPath);
            }

            var options = PantsOpenOptions.Local(databasePath).WithBackgroundCompaction(false);
            await using (var recovered = await PantsDatabase.OpenAsync(options))
            {
                Assert.Equal("value", await ReadTextAsync(recovered, "buffered"));
                Assert.True(
                    (await recovered.Diagnostics.GetRuntimeMetricsAsync()).WalLocalDurableSequence >=
                    acceptedSequence);
            }

            // Keep only the WAL image known to have crossed recovery's fsync boundary, then
            // reopen from that image as the power-loss recovery point.
            await File.WriteAllBytesAsync(walPath, durableWalImage);
            await using var afterPowerLoss = await PantsDatabase.OpenAsync(options);
            Assert.Equal("value", await ReadTextAsync(afterPowerLoss, "buffered"));
        }
        finally
        {
            child.TryKillProcessTree();
        }
    }

    [Fact]
    public async Task ShouldLeaveBufferedCommitInUnsyncedTailForParentPowerLoss()
    {
        var databasePath = Environment.GetEnvironmentVariable(DatabaseEnvironmentVariable);
        var readyPath = Environment.GetEnvironmentVariable(ReadyEnvironmentVariable);
        if (databasePath is null || readyPath is null)
        {
            return;
        }

        var failpoints = new BlockingWalSyncFailpointHandler();
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            new RuntimeTelemetry());
        using var store = LocalDiskStore.Open(databasePath, state, failpoints: failpoints);
        _ = store.AppendCommit(
            CreateCommitPayload(state, 1, "synced"),
            state,
            PantsDurability.Sync);
        var syncedWalLength = new FileInfo(Path.Combine(databasePath, "wal", "wal.log")).Length;
        failpoints.Arm();
        _ = store.AppendCommit(
            CreateCommitPayload(state, 2, "buffered"),
            state,
            PantsDurability.Buffered);
        var lostSequence = state.Sequence;
        await failpoints.WaitUntilBlockedAsync(TestTimeouts.Expected);

        await File.WriteAllLinesAsync(
            readyPath,
            [
                lostSequence.ToString(CultureInfo.InvariantCulture),
                syncedWalLength.ToString(CultureInfo.InvariantCulture)
            ]);
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    static CommitPayload CreateCommitPayload(RuntimeState state, long transactionId, string key)
    {
        var operation = new TransactionIntentOperation(
            0,
            CommitOperationKind.Put,
            DefaultFamily,
            TestBytes.FromString(key),
            null,
            TestBytes.FromString("value"),
            null,
            null,
            false);
        var source = new TransactionOperationSource(
            null,
            [operation],
            1,
            DateTimeOffset.UnixEpoch);
        return new CommitPayload(
            transactionId,
            PantsTransactionMode.ReadWrite,
            PantsConflictPolicy.LastWriteWins,
            DateTimeOffset.UnixEpoch,
            state.CreateVersion(),
            source,
            []);
    }

    static async Task CommitSyncAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString("value"));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async Task<string?> ReadTextAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(TestBytes.FromString(key));
        return value is { } bytes ? TestBytes.ToText(bytes) : null;
    }

    static async Task WaitForLockReleaseAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Expected);
        while (true)
        {
            try
            {
                using var stream = new FileStream(
                    Path.Combine(path, "LOCK"),
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return;
            }
            catch (IOException) when (!timeout.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }
        }
    }

    static async Task ExpireCrashedLeaseAsync(string path)
    {
        var leasePath = Path.Combine(path, ".midge_leader");
        var lines = await File.ReadAllLinesAsync(leasePath);
        var updated = false;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith("acquired_at: ", StringComparison.Ordinal))
            {
                continue;
            }

            lines[index] = "acquired_at: 1970-01-01T00:00:00Z";
            updated = true;
        }

        Assert.True(updated);
        await File.WriteAllLinesAsync(
            leasePath,
            lines.Where(static line => !line.StartsWith("checksum: ", StringComparison.Ordinal)));
        var acquisitionLockPath = Path.Combine(path, ".midge_leader.lock");
        if (File.Exists(acquisitionLockPath))
        {
            File.Delete(acquisitionLockPath);
        }
    }

    sealed class BlockingWalSyncFailpointHandler : IFailpointHandler
    {
        readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _armed;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public async Task WaitUntilBlockedAsync(TimeSpan timeout) => await _blocked.Task.WaitAsync(timeout);

        public void Hit(Failpoint failpoint)
        {
            if (failpoint != Failpoint.BeforeWalSync || Interlocked.Exchange(ref _armed, 0) != 1)
            {
                return;
            }

            _ = _blocked.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
        }
    }

    sealed class RecoveryWalSyncProbe(string walPath, bool throwOnSync = false) : IFailpointHandler
    {
        public int SyncAttempts { get; private set; }

        public long WalLengthAtSync { get; private set; }

        public void Hit(Failpoint failpoint)
        {
            if (failpoint != Failpoint.BeforeWalSync)
            {
                return;
            }

            SyncAttempts++;
            WalLengthAtSync = new FileInfo(walPath).Length;
            if (throwOnSync)
            {
                throw new IOException("Injected recovery WAL sync failure.");
            }
        }
    }
}
