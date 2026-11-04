using System.Diagnostics;
using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

[Collection(CrashProcessTestGroup.Name)]
public sealed class RecoveryCheckpointCrashTests
{
    const string DatabaseEnvironmentVariable = "PANTS_RECOVERY_CHECKPOINT_CRASH_DATABASE";
    const string ReadyEnvironmentVariable = "PANTS_RECOVERY_CHECKPOINT_CRASH_READY";
    const string StorageEnvironmentVariable = "PANTS_RECOVERY_CHECKPOINT_CRASH_STORAGE";
    const string BoundaryEnvironmentVariable = "PANTS_RECOVERY_CHECKPOINT_CRASH_BOUNDARY";
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
        var boundary = Environment.GetEnvironmentVariable(BoundaryEnvironmentVariable);
        var failpoint = StringComparer.Ordinal.Equals(boundary, "cloud-before-eviction")
            ? Failpoint.AfterRecoveryCheckpointPublishedBeforeLocalEviction
            : Failpoint.AfterRecoveryCheckpointPublished;
        var requiredCount = failpoint == Failpoint.AfterRecoveryCheckpointPublished
            ? CrashAfterCheckpoint
            : 1;
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(path, storage == "simulated-cloud").WithMemtableLimits(16 * 1024),
            new RuntimeDependencies(new CrashAfterCheckpointHandler(path, readyPath,
                storage == "simulated-cloud", failpoint, requiredCount)));

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
        start.Environment[BoundaryEnvironmentVariable] = "after-checkpoint";
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

        if (simulatedCloud)
        {
            Assert.Empty(Directory.GetFiles(Path.Combine(databasePath, "sst"), "*.sst"));
            Assert.True(
                Directory.GetFiles(Path.Combine(databasePath, "cloud_store", "sst"), "*.sst").Length >=
                CrashAfterCheckpoint,
                "The child must have published and evicted checkpoint SSTs before it exited.");
        }
        else
        {
            Assert.True(
                Directory.GetFiles(Path.Combine(databasePath, "sst"), "*.sst").Length >= CrashAfterCheckpoint,
                "The child must have published checkpoints before it exited.");
        }
        Assert.Equal(walBytesBefore, WalBytes(databasePath));

        var captured = Assert.IsType<CrashChildProcess.WriterLeaseSnapshot>(
            JsonSerializer.Deserialize<CrashChildProcess.WriterLeaseSnapshot>(await File.ReadAllTextAsync(readyPath)));
        await CrashChildProcess.ExpireStoppedWriterLeaseAsync(databasePath, captured);
        await using var reopened = await PantsDatabase.OpenAsync(CreateOptions(databasePath, simulatedCloud));
        var second = Assert.IsType<IPantsColumnFamily>(
            await reopened.ColumnFamilies.GetAsync(SecondFamily.Name),
            false);
        await AssertEveryTransactionVisibleOnceAsync(reopened, reopened.ColumnFamilies.DefaultFamily, "default");
        await AssertEveryTransactionVisibleOnceAsync(reopened, second, "second");
    }

    [Fact]
    public async Task ShouldRecoverExactlyOnceAfterCloudCheckpointPublicationBeforeLocalEviction()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "db");
        var readyPath = Path.Combine(directory.Path, "checkpoint.ready");
        WriteUnflushedCrossFamilyTransactions(databasePath);

        var start = CrashChildProcess.CreateStartInfo(
            typeof(RecoveryCheckpointCrashTests),
            nameof(ShouldExitAfterDurableRecoveryCheckpointInChild));
        start.Environment[DatabaseEnvironmentVariable] = databasePath;
        start.Environment[ReadyEnvironmentVariable] = readyPath;
        start.Environment[StorageEnvironmentVariable] = "simulated-cloud";
        start.Environment[BoundaryEnvironmentVariable] = "cloud-before-eviction";
        using (var child = await CrashChildProcess.StartAndWaitForReadinessAsync(
                   start,
                   "cloud recovery checkpoint publication child",
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

        Assert.NotEmpty(Directory.GetFiles(Path.Combine(databasePath, "sst"), "*.sst"));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(databasePath, "cloud_store", "sst"), "*.sst"));

        var captured = Assert.IsType<CrashChildProcess.WriterLeaseSnapshot>(
            JsonSerializer.Deserialize<CrashChildProcess.WriterLeaseSnapshot>(await File.ReadAllTextAsync(readyPath)));
        await CrashChildProcess.ExpireStoppedWriterLeaseAsync(databasePath, captured);
        await using var reopened = await PantsDatabase.OpenAsync(CreateOptions(databasePath, true));
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
            var peer = await reader.GetAsync(TestBytes.FromString($"peer-{index:D3}"));
            Assert.NotNull(peer);
            Assert.Equal(ValueFor(prefix, index), peer.Value.ToArray());
        }

        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var count = 0;
        await foreach (var _ in scan)
        {
            count++;
        }

        Assert.Equal(TransactionCount * 2, count);
    }

    /// <summary>
    ///     Each family receives its own legal two-key transaction. Checkpoints may separate family
    ///     frontiers, but must not lose either key or replay any transaction twice.
    /// </summary>
    static void WriteUnflushedCrossFamilyTransactions(string root)
    {
        var state = NewState();
        using var store = LocalDiskStore.Open(root, state);
        store.CreateColumnFamily(SecondFamily);
        for (var index = 0; index < TransactionCount; index++)
        {
            foreach (var family in new[] { DefaultFamily, SecondFamily })
            {
                _ = store.AppendCommit(
                    CreateCommitPayload(
                        state,
                        [
                            CreatePut(0, family, TestBytes.FromString($"key-{index:D3}"), ValueFor(family.Name, index)),
                            CreatePut(1, family, TestBytes.FromString($"peer-{index:D3}"), ValueFor(family.Name, index))
                        ]),
                    state,
                    PantsDurability.Sync);
            }
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

    sealed class CrashAfterCheckpointHandler(
        string databasePath,
        string readyPath,
        bool simulatedCloud,
        Failpoint targetFailpoint,
        int requiredCount) : IFailpointHandler
    {
        int _checkpoints;

        public void Hit(Failpoint failpoint)
        {
            if (failpoint != targetFailpoint || ++_checkpoints < requiredCount)
            {
                return;
            }

            var captured = CrashChildProcess.CaptureWriterLeaseAsync(databasePath, simulatedCloud)
                .GetAwaiter().GetResult();
            File.WriteAllText(readyPath, JsonSerializer.Serialize(captured));
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
