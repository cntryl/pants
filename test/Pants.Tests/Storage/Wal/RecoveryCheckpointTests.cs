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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldKeepAReplayedDeleteShadowingAFlushedValueAcrossCheckpoints(bool simulatedCloud)
    {
        using var directory = new TemporaryDirectory();
        var options = (simulatedCloud
                ? PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "checkpoint-delete/")
                : PantsOpenOptions.Local(directory.Path))
            .WithBackgroundCompaction(false);
        var doomed = TestBytes.FromString("doomed");
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await CommitAsync(database, transaction => transaction.Put(doomed, new byte[ValueBytes]));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await CommitAsync(database, transaction => transaction.Delete(doomed));
            for (var index = 0; index < CommitCount; index++)
            {
                await CommitAsync(
                    database,
                    transaction => transaction.Put(
                        TestBytes.FromString($"key-{index:D3}"),
                        new byte[ValueBytes]));
            }
        }

        var sstsBefore = Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst").Length;
        await using (var checkpointed = await PantsDatabase.OpenAsync(options.WithMemtableLimits(16 * 1024)))
        {
            Assert.True(
                Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst").Length > sstsBefore,
                "Replay should have published checkpoints.");
            Assert.Null(await ReadAsync(checkpointed, doomed));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Null(await ReadAsync(reopened, doomed));
        Assert.NotNull(await ReadAsync(reopened, TestBytes.FromString($"key-{CommitCount - 1:D3}")));
    }

    static async Task CommitAsync(IPantsDatabase database, Action<IPantsTransaction> write)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        write(transaction);
        await transaction.CommitAsync(
            database.Cloud is null ? PantsWriteOptions.Sync : PantsWriteOptions.CloudStrict);
    }

    static async Task<ReadOnlyMemory<byte>?> ReadAsync(IPantsDatabase database, byte[] key)
    {
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        return await reader.GetAsync(key);
    }

    [Fact]
    public void ShouldCheckpointWithinHalfTheFreeLocalStorageWhenReplayingUnderALocalBudget()
    {
        using var directory = new TemporaryDirectory();
        WriteUnflushedCommits(directory.Path, CommitCount);
        var walBytesBefore = WalBytes(directory.Path);
        var state = NewState();
        using var hybridCache = new HybridCacheManager(walBytesBefore + 64 * 1024);

        using (LocalDiskStore.Open(
                   directory.Path,
                   state,
                   recoveryCheckpointBytes: 16 * 1024 * 1024,
                   hybridCache: hybridCache))
        {
            Assert.True(
                Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst").Length >= 2,
                "The free local storage, not the memtable target, should bound each checkpoint.");
            Assert.True(state.ActiveMemtableBytes.Values.Sum() <= 32 * 1024 + ValueBytes + 1024);
            Assert.Equal(0, hybridCache.Ledger.ReservedBytes);
        }

        Assert.Equal(walBytesBefore, WalBytes(directory.Path));
    }

    [Fact]
    public async Task ShouldFailWithNoSpaceAndLeaveWalUntouchedWhenTheRecoverySpoolExceedsTheLocalBudget()
    {
        using var directory = new TemporaryDirectory();
        using (LocalDiskStore.Open(directory.Path, NewState()))
        {
        }

        var payloads = new List<byte[]>
        {
            WalCodec.EncodeTransactionMarker(WalOperation.TransactionBegin, 9, 1, 1)
        };
        for (var index = 0; index < 8; index++)
        {
            payloads.Add(WalCodec.EncodeTransactionMutation(
                new WalMutation(
                    0,
                    WalOperation.Put,
                    TestBytes.FromString($"spooled-{index}"),
                    new byte[16 * 1024],
                    checked((ulong)index + 2),
                    null,
                    null),
                9,
                1));
        }

        payloads.Add(WalCodec.EncodeTransactionMarker(WalOperation.TransactionCommit, 9, 10, 1));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "wal", "wal.log"), Frame(payloads));
        var walBytesBefore = WalBytes(directory.Path);

        using (var hybridCache = new HybridCacheManager(walBytesBefore + 32 * 1024))
        {
            Assert.Throws<PantsNoSpaceException>(() => LocalDiskStore.Open(
                directory.Path,
                NewState(),
                recoveryPolicy: PantsRecoveryPolicy.Salvage,
                recoveryCheckpointBytes: 1024 * 1024,
                hybridCache: hybridCache));
        }

        Assert.Equal(walBytesBefore, WalBytes(directory.Path));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "wal"), "*.corrupt*"));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "wal"), "*.salvage-retained*"));

        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 8; index++)
        {
            var value = await reader.GetAsync(TestBytes.FromString($"spooled-{index}"));
            Assert.Equal(16 * 1024, value!.Value.Length);
        }
    }

    static byte[] Frame(IEnumerable<byte[]> payloads)
    {
        using var stream = new MemoryStream();
        foreach (var payload in payloads)
        {
            DiskFormat.WriteUInt32(stream, checked((uint)payload.Length));
            DiskFormat.WriteUInt32(stream, DiskFormat.Crc32C(payload));
            stream.Write(payload);
        }

        return stream.ToArray();
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
