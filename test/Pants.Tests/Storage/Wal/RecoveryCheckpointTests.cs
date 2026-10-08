using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class RecoveryCheckpointTests
{
    const int CommitCount = 40;
    const int ValueBytes = 4096;

    static readonly ColumnFamilyIdentity DefaultFamily = new(
        0,
        "default",
        RuntimeState.DefaultFamilyVersion);

    [Fact]
    public async Task ShouldCheckpointDuringReplayAndRecoverEveryValueExactlyOnceAfterReopen()
    {
        using var directory = new TemporaryDirectory();
        WriteUnflushedCommits(directory.Path, CommitCount);
        var walBytesBefore = WalBytes(directory.Path);
        var state = NewState();

        using (var recovering = LocalDiskStore.Open(
                   directory.Path,
                   state,
                   recoveryCheckpointBytes: 16 * 1024))
        {
            Assert.True(
                Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst").Length >= 2,
                "Replay should have published several checkpoints.");
            Assert.True(
                state.ActiveMemtableBytes.Values.Sum() < CommitCount * ValueBytes / 2,
                "Recovered memory should have been released at checkpoints.");
        }

        Assert.Equal(walBytesBefore, WalBytes(directory.Path));
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < CommitCount; index++)
        {
            var value = await reader.GetAsync(TestBytes.FromString($"key-{index:D3}"));
            Assert.Equal(ValueBytes, value!.Value.Length);
        }

        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var count = 0;
        await foreach (var _ in scan)
        {
            count++;
        }

        Assert.Equal(CommitCount, count);
    }

    [Fact]
    public void ShouldFailWithResourceLimitAndLeaveWalUntouchedWhenOneTransactionCannotFit()
    {
        using var directory = new TemporaryDirectory();
        WriteUnflushedCommits(directory.Path, 1, valueBytes: 256 * 1024);
        var walBytesBefore = WalBytes(directory.Path);

        Assert.Throws<PantsResourceLimitException>(() => LocalDiskStore.Open(
            directory.Path,
            NewState(),
            recoveryPolicy: PantsRecoveryPolicy.Salvage,
            recoveryCheckpointBytes: 1024));

        Assert.Equal(walBytesBefore, WalBytes(directory.Path));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "wal"), "*.corrupt*"));
    }

    static long WalBytes(string root) =>
        Directory.GetFiles(Path.Combine(root, "wal"))
            .Sum(static path => new FileInfo(path).Length);

    static RuntimeState NewState() => new(
        new ManualClock(DateTimeOffset.UnixEpoch),
        new RuntimeTelemetry());

    static void WriteUnflushedCommits(string root, int count, int valueBytes = ValueBytes)
    {
        var state = NewState();
        using var store = LocalDiskStore.Open(root, state);
        for (var index = 0; index < count; index++)
        {
            _ = store.AppendCommit(
                CreateCommitPayload(state, $"key-{index:D3}", valueBytes),
                state,
                PantsDurability.Sync);
        }
    }

    static CommitPayload CreateCommitPayload(RuntimeState state, string key, int valueBytes)
    {
        var operation = new TransactionIntentOperation(
            0,
            CommitOperationKind.Put,
            DefaultFamily,
            TestBytes.FromString(key),
            null,
            new byte[valueBytes],
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
