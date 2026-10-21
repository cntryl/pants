using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeTransactionBasicParityTests
{
    static readonly string[] AllModes = ["memory", "local", "cloud"];
    static readonly string[] DurableModes = ["local", "cloud"];

    [Fact]
    public async Task ShouldCommitAtomicallyGivenMixedPutAndDeleteRangeWhenCommittedInTransaction()
    {
        await ForEachDatabaseAsync(AllModes, async (mode, database, family) =>
        {
            await PutAsync(database, family, mode,
                ("key1", "v1"), ("key2", "v2"), ("key3", "v3"));
            await using (var transaction = await BeginWriteAsync(database, family))
            {
                transaction.Put("key4"u8.ToArray(), "v4"u8.ToArray());
                transaction.Delete("key3"u8.ToArray());
                transaction.DeleteRange("key1"u8.ToArray(), "key3"u8.ToArray());
                await transaction.CommitAsync(WriteOptions(mode));
            }

            await using var reader = await BeginReadAsync(database, family);
            Assert.Null(await reader.GetAsync("key1"u8.ToArray()));
            Assert.Null(await reader.GetAsync("key2"u8.ToArray()));
            Assert.Null(await reader.GetAsync("key3"u8.ToArray()));
            Assert.Equal("v4", ReadText(await reader.GetAsync("key4"u8.ToArray())));
        });
    }

    [Fact]
    public async Task ShouldCommitTransactionGivenMultipleOperationsWhenCommitted()
    {
        await ForEachDatabaseAsync(AllModes, async (mode, database, family) =>
        {
            await using var transaction = await BeginWriteAsync(database, family);
            transaction.Put("key1"u8.ToArray(), "value1"u8.ToArray());
            transaction.Put("key2"u8.ToArray(), "value2"u8.ToArray());
            transaction.Delete("key3"u8.ToArray());
            await transaction.CommitAsync(WriteOptions(mode));

            await using var reader = await BeginReadAsync(database, family);
            Assert.Equal("value1", ReadText(await reader.GetAsync("key1"u8.ToArray())));
            Assert.Equal("value2", ReadText(await reader.GetAsync("key2"u8.ToArray())));
            Assert.Null(await reader.GetAsync("key3"u8.ToArray()));
        });
    }

    [Fact]
    public async Task ShouldDeleteRangeGivenCommittedEngineOperationWhenDeleteRange()
    {
        await ForEachDatabaseAsync(AllModes, async (mode, database, family) =>
        {
            await PutAsync(database, family, mode,
                ("key1", "v1"), ("key2", "v2"), ("key3", "v3"));
            await using (var deleting = await BeginWriteAsync(database, family))
            {
                deleting.DeleteRange("key1"u8.ToArray(), "key3"u8.ToArray());
                await deleting.CommitAsync(WriteOptions(mode));
            }

            await using var reader = await BeginReadAsync(database, family);
            Assert.Null(await reader.GetAsync("key1"u8.ToArray()));
            Assert.Null(await reader.GetAsync("key2"u8.ToArray()));
            Assert.Equal("v3", ReadText(await reader.GetAsync("key3"u8.ToArray())));
        });
    }

    [Fact]
    public async Task ShouldHideDeletedKeyGivenDeleteRangeAndPointPutWhenScanning()
    {
        await ForEachDatabaseAsync(AllModes, async (mode, database, family) =>
        {
            await PutAsync(database, family, mode,
                ("key1", "v1"), ("key2", "v2"), ("key3", "v3"));
            await using (var deleting = await BeginWriteAsync(database, family))
            {
                deleting.DeleteRange("key1"u8.ToArray(), "key3"u8.ToArray());
                await deleting.CommitAsync(WriteOptions(mode));
            }

            await using var reader = await BeginReadAsync(database, family);
            await using var scan = await reader.ScanAsync(new PantsScanQuery
            {
                StartInclusive = "key0"u8.ToArray(),
                EndExclusive = "key9"u8.ToArray()
            });
            var entry = Assert.Single(await CollectAsync(scan));
            Assert.Equal("key3", TestBytes.ToText(entry.Key));
            Assert.Equal("v3", TestBytes.ToText(entry.Value));
        });
    }

    [Fact]
    public async Task ShouldHideDeletedValueGivenKvTransactionWhenGetting()
    {
        await ForEachDatabaseAsync(AllModes, async (_, database, family) =>
        {
            await using var transaction = await BeginWriteAsync(database, family);
            transaction.Put("k"u8.ToArray(), "v"u8.ToArray());
            transaction.Delete("k"u8.ToArray());

            Assert.Null(await transaction.GetAsync("k"u8.ToArray()));
        });
    }

    [Fact]
    public async Task ShouldInsertValueGivenNonexistentKeyWhenInsertInTransaction()
    {
        await ForEachDatabaseAsync(AllModes, async (mode, database, family) =>
        {
            await using (var transaction = await BeginWriteAsync(database, family))
            {
                transaction.Insert("key1"u8.ToArray(), "value1"u8.ToArray());
                await transaction.CommitAsync(WriteOptions(mode));
            }

            Assert.Equal("value1", await ReadAsync(database, family, "key1"));
        });
    }

    [Fact]
    public async Task ShouldPreserveWriteSetSemanticsGivenDeletePutDeleteSequenceWhenReadingOwnWrites()
    {
        await ForEachDatabaseAsync(AllModes, async (_, database, family) =>
        {
            await using var transaction = await BeginWriteAsync(database, family);
            transaction.Delete("key"u8.ToArray());
            transaction.Put("key"u8.ToArray(), "two"u8.ToArray());
            transaction.Delete("key"u8.ToArray());

            Assert.Null(await transaction.GetAsync("key"u8.ToArray()));
        });
    }

    [Fact]
    public async Task ShouldPreserveWriteSetSemanticsGivenPutDeletePutSequenceWhenReadingOwnWrites()
    {
        await ForEachDatabaseAsync(AllModes, async (_, database, family) =>
        {
            await using var transaction = await BeginWriteAsync(database, family);
            transaction.Put("key"u8.ToArray(), "one"u8.ToArray());
            transaction.Delete("key"u8.ToArray());
            transaction.Put("key"u8.ToArray(), "three"u8.ToArray());

            Assert.Equal("three", ReadText(await transaction.GetAsync("key"u8.ToArray())));
        });
    }

    [Fact]
    public async Task ShouldReadOwnWritesGivenKvTransactionWhenGetting()
    {
        await ForEachDatabaseAsync(AllModes, async (_, database, family) =>
        {
            await using var transaction = await BeginWriteAsync(database, family);
            transaction.Put("test_key"u8.ToArray(), "test_value"u8.ToArray());

            Assert.Equal("test_value", ReadText(await transaction.GetAsync("test_key"u8.ToArray())));
        });
    }

    [Fact]
    public async Task ShouldReadOwnWritesGivenTransactionWhenReading()
    {
        await ForEachDatabaseAsync(AllModes, async (mode, database, family) =>
        {
            await using var transaction = await BeginWriteAsync(database, family);
            transaction.Put("key1"u8.ToArray(), "value1"u8.ToArray());
            Assert.Equal("value1", ReadText(await transaction.GetAsync("key1"u8.ToArray())));
            await transaction.CommitAsync(WriteOptions(mode));
        });
    }

    [Fact]
    public async Task ShouldRecoverCommittedTransactionsGivenWalReplayWhenRestart()
    {
        await ForEachModeAsync(DurableModes, async (mode, path) =>
        {
            await using (var database = await OpenDatabaseAsync(mode, path))
            {
                var family = await database.ColumnFamilies.CreateAsync("test");
                await PutAsync(database, family, mode, ("key1", "value1"));
                await PutAsync(database, family, mode, ("key2", "value2"));
            }

            await using var reopened = await OpenDatabaseAsync(mode, path);
            var reopenedFamily = await reopened.ColumnFamilies.GetAsync("test");
            Assert.NotNull(reopenedFamily);
            Assert.Equal("value1", await ReadAsync(reopened, reopenedFamily, "key1"));
            Assert.Equal("value2", await ReadAsync(reopened, reopenedFamily, "key2"));
        });
    }

    [Fact]
    public async Task ShouldRejectCloudStrictGivenEmptyNonCloudTransactionWhenCommitted()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenDatabaseAsync("memory", directory.Path);
        var family = await database.ColumnFamilies.CreateAsync("test");
        await using var transaction = await BeginWriteAsync(database, family);

        await Assert.ThrowsAsync<PantsNotSupportedException>(() =>
            transaction.CommitAsync(PantsWriteOptions.CloudStrict).AsTask());
    }

    [Fact]
    public async Task ShouldRejectDeleteGivenReadOnlyTransactionWhenCalled()
    {
        await ForEachDatabaseAsync(AllModes, async (_, database, family) =>
        {
            await using var transaction = await BeginReadAsync(database, family);
            var error = Assert.ThrowsAny<PantsException>(() => transaction.Delete("key"u8.ToArray()));
            Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
        });
    }

    [Fact]
    public async Task ShouldRejectDeleteRangeGivenReadOnlyTransactionWhenCalled()
    {
        await ForEachDatabaseAsync(AllModes, async (_, database, family) =>
        {
            await using var transaction = await BeginReadAsync(database, family);
            var error = Assert.ThrowsAny<PantsException>(() =>
                transaction.DeleteRange("a"u8.ToArray(), "z"u8.ToArray()));
            Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
        });
    }

    [Fact]
    public async Task ShouldRejectDeleteRangeGivenReversedBoundsWhenAddedToTransaction()
    {
        await ForEachDatabaseAsync(AllModes, async (_, database, family) =>
        {
            await using var transaction = await BeginWriteAsync(database, family);
            var error = Assert.ThrowsAny<PantsException>(() =>
                transaction.DeleteRange("key9"u8.ToArray(), "key1"u8.ToArray()));
            Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
        });
    }

    [Fact]
    public async Task ShouldRejectInsertGivenReadOnlyTransactionWhenCalled()
    {
        await ForEachDatabaseAsync(AllModes, async (_, database, family) =>
        {
            await using var transaction = await BeginReadAsync(database, family);
            var error = Assert.ThrowsAny<PantsException>(() =>
                transaction.Insert("key"u8.ToArray(), "value"u8.ToArray()));
            Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
        });
    }

    [Fact]
    public async Task ShouldRejectLocalOnlyWriteOptionsGivenCloudTransactionWhenCommitted()
    {
        await ForEachDatabaseAsync(["cloud"], async (_, database, family) =>
        {
            await using var sync = await BeginWriteAsync(database, family);
            await Assert.ThrowsAsync<PantsNotSupportedException>(() =>
                sync.CommitAsync(PantsWriteOptions.Sync).AsTask());

            await using var buffered = await BeginWriteAsync(database, family);
            await Assert.ThrowsAsync<PantsNotSupportedException>(() =>
                buffered.CommitAsync(PantsWriteOptions.Buffered).AsTask());
        });
    }

    [Fact]
    public async Task ShouldReturnNoRowsGivenZeroLimitWhenScanning()
    {
        await ForEachDatabaseAsync(AllModes, async (_, database, family) =>
        {
            await using var transaction = await BeginWriteAsync(database, family);
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await using var scan = await transaction.ScanAsync(new PantsScanQuery { Limit = 0 });

            Assert.Empty(await CollectAsync(scan));
        });
    }

    [Fact]
    public async Task ShouldRollbackAllWritesGivenMultipleOperationsWhenDropped()
    {
        await ForEachDatabaseAsync(AllModes, async (mode, database, family) =>
        {
            await PutAsync(database, family, mode, ("key1", "original"));
            await using (var transaction = await BeginWriteAsync(database, family))
            {
                transaction.Put("key1"u8.ToArray(), "updated"u8.ToArray());
                transaction.Put("key2"u8.ToArray(), "value2"u8.ToArray());
                transaction.Delete("key3"u8.ToArray());
            }

            Assert.Equal("original", await ReadAsync(database, family, "key1"));
            Assert.Null(await ReadAsync(database, family, "key2"));
        });
    }

    [Fact]
    public async Task ShouldRollbackTransactionGivenUncommittedWhenDropped()
    {
        await ForEachDatabaseAsync(AllModes, async (_, database, family) =>
        {
            await using (var transaction = await BeginWriteAsync(database, family))
            {
                transaction.Put("key1"u8.ToArray(), "value1"u8.ToArray());
            }

            Assert.Null(await ReadAsync(database, family, "key1"));
        });
    }

    [Fact]
    public async Task ShouldSeeUncommittedWritesGivenTransactionScanWhenScanning()
    {
        await ForEachDatabaseAsync(AllModes, async (_, database, family) =>
        {
            await using var transaction = await BeginWriteAsync(database, family);
            transaction.Put("key1"u8.ToArray(), "value1"u8.ToArray());
            transaction.Put("key2"u8.ToArray(), "value2"u8.ToArray());
            await using var scan = await transaction.ScanAsync(new PantsScanQuery
            {
                StartInclusive = "key0"u8.ToArray(),
                EndExclusive = "key9"u8.ToArray()
            });

            Assert.Equal(
                ["key1:value1", "key2:value2"],
                (await CollectAsync(scan)).Select(static entry =>
                    $"{TestBytes.ToText(entry.Key)}:{TestBytes.ToText(entry.Value)}"));
        });
    }

    [Fact]
    public async Task ShouldSucceedGivenEmptyCloudStrictCloudTransactionWhenCommitted()
    {
        await ForEachDatabaseAsync(["cloud"], async (_, database, family) =>
        {
            await using var transaction = await BeginWriteAsync(database, family);
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
        });
    }

    [Fact]
    public async Task ShouldSucceedGivenEmptyTransactionWhenCommitted()
    {
        await ForEachDatabaseAsync(AllModes, async (mode, database, family) =>
        {
            await using var transaction = await BeginWriteAsync(database, family);
            await transaction.CommitAsync(WriteOptions(mode));
        });
    }

    [Fact]
    public async Task ShouldSupportBestEffortDuringBulkLoadPhaseWhenFollowedByFlush()
    {
        await ForEachDatabaseAsync(AllModes, async (mode, database, family) =>
        {
            for (var index = 0; index < 1_000; index++)
            {
                await using var transaction = await BeginWriteAsync(database, family);
                transaction.Put(
                    TestBytes.FromString($"bulk_{index:00000}"),
                    TestBytes.FromString($"data_{index * 2}"));
                await transaction.CommitAsync(PantsWriteOptions.BestEffort);
            }

            await database.Maintenance.FlushAsync(family);
            await PutAsync(database, family, mode, ("measured_key", "measured_value"));

            Assert.Equal("data_0", await ReadAsync(database, family, "bulk_00000"));
            Assert.Equal("data_1000", await ReadAsync(database, family, "bulk_00500"));
            Assert.Equal("data_1998", await ReadAsync(database, family, "bulk_00999"));
            Assert.Equal("measured_value", await ReadAsync(database, family, "measured_key"));
        });
    }

    [Fact]
    public async Task ShouldUnregisterSnapshotGivenCommitFailureWhenCommitReturnsError()
    {
        await ForEachDatabaseAsync(AllModes, async (mode, database, family) =>
        {
            await using var transaction = await BeginWriteAsync(database, family);
            transaction.Put("key1"u8.ToArray(), "value1"u8.ToArray());
            Assert.Equal(1, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);

            var incompatibleOptions = mode == "cloud"
                ? PantsWriteOptions.Sync
                : PantsWriteOptions.CloudStrict;
            await Assert.ThrowsAsync<PantsNotSupportedException>(() =>
                transaction.CommitAsync(incompatibleOptions).AsTask());

            Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
            await PutAsync(database, family, mode, ("key2", "value2"));
            Assert.Equal("value2", await ReadAsync(database, family, "key2"));
        });
    }

    [Fact]
    public async Task ShouldUnregisterSnapshotGivenCommitOfReadOnlyTransactionWhenCommitting()
    {
        await ForEachDatabaseAsync(AllModes, async (mode, database, family) =>
        {
            await PutAsync(database, family, mode, ("key1", "value1"));
            await using var transaction = await BeginReadAsync(database, family);
            Assert.Equal(1, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
            _ = await transaction.GetAsync("key1"u8.ToArray());

            await transaction.CommitAsync(WriteOptions(mode));

            Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
        });
    }

    static async Task ForEachDatabaseAsync(
        string[] modes,
        Func<string, IPantsDatabase, IPantsColumnFamily, Task> scenario)
    {
        foreach (var mode in modes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenDatabaseAsync(mode, directory.Path);
            var family = await database.ColumnFamilies.CreateAsync("test");
            await scenario(mode, database, family);
        }
    }

    static async Task ForEachModeAsync(string[] modes, Func<string, string, Task> scenario)
    {
        foreach (var mode in modes)
        {
            using var directory = new TemporaryDirectory();
            await scenario(mode, directory.Path);
        }
    }

    static ValueTask<IPantsDatabase> OpenDatabaseAsync(string mode, string path) => mode switch
    {
        "memory" => PantsDatabase.OpenAsync(PantsOpenOptions.InMemory()),
        "local" => PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(path).WithBackgroundCompaction(false)),
        "cloud" => PantsDatabase.OpenAsync(
            PantsOpenOptions.SimulatedCloud(path, "pants-tests", "midge-transaction-basic/")
                .WithBackgroundCompaction(false)),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown storage mode.")
    };

    static ValueTask<IPantsTransaction> BeginWriteAsync(
        IPantsDatabase database,
        IPantsColumnFamily family) =>
        database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite);

    static ValueTask<IPantsTransaction> BeginReadAsync(
        IPantsDatabase database,
        IPantsColumnFamily family) =>
        database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly);

    static PantsWriteOptions WriteOptions(string mode) =>
        mode == "cloud" ? PantsWriteOptions.CloudAsync : PantsWriteOptions.Buffered;

    static async Task PutAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string mode,
        params (string Key, string Value)[] entries)
    {
        await using var transaction = await BeginWriteAsync(database, family);
        foreach (var (key, value) in entries)
        {
            transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        }

        await transaction.CommitAsync(WriteOptions(mode));
    }

    static async Task<string?> ReadAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key)
    {
        await using var transaction = await BeginReadAsync(database, family);
        return ReadText(await transaction.GetAsync(TestBytes.FromString(key)));
    }

    static string? ReadText(ReadOnlyMemory<byte>? value) =>
        value is { } present ? TestBytes.ToText(present) : null;

    static async Task<List<PantsEntry>> CollectAsync(IPantsScan scan)
    {
        var entries = new List<PantsEntry>();
        await foreach (var entry in scan)
        {
            entries.Add(entry);
        }

        return entries;
    }
}
