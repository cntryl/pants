using System.Diagnostics;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

[Collection(CrashProcessTestGroup.Name)]
public sealed class RecoveryCheckpointCrashTests
{
    const string DatabaseEnvironmentVariable = "PANTS_RECOVERY_CHECKPOINT_CRASH_DATABASE";
    const string ReadyEnvironmentVariable = "PANTS_RECOVERY_CHECKPOINT_CRASH_READY";
    const string StorageEnvironmentVariable = "PANTS_RECOVERY_CHECKPOINT_CRASH_STORAGE";
    const int CrashAfterCheckpoint = 2;
    const int TransactionCount = 48;
    const int ValueBytes = 2048;

    static readonly ColumnFamilyIdentity DefaultFamily = new(
        0,
        "default",
        RuntimeState.DefaultFamilyVersion);

    static readonly ColumnFamilyIdentity SecondFamily = new(1, "second", 1);

    [Fact]
    public async Task ShouldExitAfterDurableRecoveryCheckpointInChild()
    {
        var storage = Environment.GetEnvironmentVariable(StorageEnvironmentVariable);
        if (storage is not ("local" or "simulated-cloud"))
        {
            return;
        }

        var path = Assert.IsType<string>(Environment.GetEnvironmentVariable(DatabaseEnvironmentVariable));
        var readyPath = Assert.IsType<string>(Environment.GetEnvironmentVariable(ReadyEnvironmentVariable));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(path, storage == "simulated-cloud").WithMemtableLimits(16 * 1024),
            new RuntimeDependencies(new CrashAfterCheckpointHandler(readyPath)));

        Assert.Fail("Recovery finished without reaching the crash checkpoint.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRecoverEveryCrossFamilyWriteExactlyOnceAfterExitingBetweenCheckpoints(
        bool simulatedCloud)
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "db");
        var readyPath = Path.Combine(directory.Path, "checkpoint.ready");
        WriteUnflushedCrossFamilyTransactions(databasePath);
        var walBytesBefore = WalBytes(databasePath);

        var start = CrashChildProcess.CreateStartInfo(
            typeof(RecoveryCheckpointCrashTests),
            nameof(ShouldExitAfterDurableRecoveryCheckpointInChild));
        start.Environment[DatabaseEnvironmentVariable] = databasePath;
        start.Environment[ReadyEnvironmentVariable] = readyPath;
        start.Environment[StorageEnvironmentVariable] = simulatedCloud ? "simulated-cloud" : "local";
        using (var child = await CrashChildProcess.StartAndWaitForReadinessAsync(
                   start,
                   "recovery checkpoint crash child",
                   databasePath,
                   readyPath))
        {
            try
            {
                await child.Process.WaitForExitAsync().WaitAsync(TestTimeouts.Expected);
            }
            finally
            {
                child.TryKillProcessTree();
            }

            Assert.True(child.ExitCode != 0, await child.DescribeAsync());
        }

        Assert.True(
            Directory.GetFiles(Path.Combine(databasePath, "sst"), "*.sst").Length >= CrashAfterCheckpoint,
            "The child must have published checkpoints before it exited.");
        Assert.Equal(walBytesBefore, WalBytes(databasePath));

        await WaitForLockReleaseAsync(databasePath);
        await ExpireCrashedLeaseAsync(databasePath);
        await using var reopened = await PantsDatabase.OpenAsync(CreateOptions(databasePath, simulatedCloud));
        var second = Assert.IsType<IPantsColumnFamily>(
            await reopened.ColumnFamilies.GetAsync(SecondFamily.Name),
            false);
        await AssertEveryTransactionVisibleOnceAsync(reopened, reopened.ColumnFamilies.DefaultFamily, "default");
        await AssertEveryTransactionVisibleOnceAsync(reopened, second, "second");
    }

    static PantsOpenOptions CreateOptions(string path, bool simulatedCloud) =>
        (simulatedCloud
            ? PantsOpenOptions.SimulatedCloud(path, "pants-tests", "recovery-checkpoint/")
            : PantsOpenOptions.Local(path))
        .WithBackgroundCompaction(false);

    static async Task AssertEveryTransactionVisibleOnceAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string prefix)
    {
        await using var reader = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < TransactionCount; index++)
        {
            var value = await reader.GetAsync(TestBytes.FromString($"key-{index:D3}"));
            Assert.NotNull(value);
            Assert.Equal(ValueFor(prefix, index), value.Value.ToArray());
        }

        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var count = 0;
        await foreach (var _ in scan)
        {
            count++;
        }

        Assert.Equal(TransactionCount, count);
    }

    /// <summary>
    ///     Each transaction writes the same key to both families in one WAL batch, so a recovery that
    ///     made only one family's half visible would show up as a missing or mismatched value.
    /// </summary>
    static void WriteUnflushedCrossFamilyTransactions(string root)
    {
        var state = NewState();
        using var store = LocalDiskStore.Open(root, state);
        store.CreateColumnFamily(SecondFamily);
        for (var index = 0; index < TransactionCount; index++)
        {
            var key = TestBytes.FromString($"key-{index:D3}");
            _ = store.AppendCommit(
                CreateCommitPayload(
                    state,
                    [
                        CreatePut(0, DefaultFamily, key, ValueFor("default", index)),
                        CreatePut(1, SecondFamily, key, ValueFor("second", index))
                    ]),
                state,
                PantsDurability.Sync);
        }
    }

    static byte[] ValueFor(string prefix, int index)
    {
        var value = new byte[ValueBytes];
        TestBytes.FromString($"{prefix}-{index:D3}").CopyTo(value, 0);
        return value;
    }

    static TransactionIntentOperation CreatePut(
        ulong ordinal,
        ColumnFamilyIdentity family,
        byte[] key,
        byte[] value) =>
        new(
            ordinal,
            CommitOperationKind.Put,
            family,
            key,
            null,
            value,
            null,
            null,
            false);

    static CommitPayload CreateCommitPayload(
        RuntimeState state,
        TransactionIntentOperation[] operations) =>
        new(
            1,
            PantsTransactionMode.ReadWrite,
            PantsConflictPolicy.LastWriteWins,
            DateTimeOffset.UnixEpoch,
            state.CreateVersion(),
            new TransactionOperationSource(null, operations, (ulong)operations.Length, DateTimeOffset.UnixEpoch),
            []);

    static RuntimeState NewState() => new(
        new ManualClock(DateTimeOffset.UnixEpoch),
        new RuntimeTelemetry());

    static long WalBytes(string root) =>
        Directory.GetFiles(Path.Combine(root, "wal"))
            .Sum(static path => new FileInfo(path).Length);

    static async Task WaitForLockReleaseAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Expected);
        while (true)
        {
            try
            {
                await using var stream = new FileStream(
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
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith("acquired_at: ", StringComparison.Ordinal))
            {
                lines[index] = "acquired_at: 1970-01-01T00:00:00Z";
            }
        }

        // The edit invalidates the record checksum, so the expired copy carries none.
        await File.WriteAllLinesAsync(
            leasePath,
            lines.Where(static line => !line.StartsWith("checksum: ", StringComparison.Ordinal)));
        File.Delete(Path.Combine(path, ".midge_leader.lock"));
    }

    sealed class CrashAfterCheckpointHandler(string readyPath) : IFailpointHandler
    {
        int _checkpoints;

        public void Hit(Failpoint failpoint)
        {
            if (failpoint != Failpoint.AfterRecoveryCheckpointPublished ||
                ++_checkpoints < CrashAfterCheckpoint)
            {
                return;
            }

            File.WriteAllText(readyPath, "ready");
            using var process = Process.GetCurrentProcess();
            try
            {
                process.Kill();
            }
            finally
            {
                Environment.Exit(137);
            }
        }
    }
}
