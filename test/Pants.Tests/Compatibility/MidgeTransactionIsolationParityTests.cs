using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeTransactionIsolationParityTests
{
    static readonly string[] AllModes = ["memory", "local", "cloud"];

    [Fact]
    public async Task ShouldPreventDirtyReadGivenUncommittedWriteWhenReading()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await using var writer = await BeginWriteAsync(database);
            writer.Put("key"u8.ToArray(), "uncommitted"u8.ToArray());
            await using var reader = await BeginReadAsync(database);

            Assert.Null(await reader.GetAsync("key"u8.ToArray()));
        });
    }

    [Fact]
    public async Task ShouldNotSeeUncommittedWriteGivenConcurrentTransactionWhenReading()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await using var writer = await BeginWriteAsync(database);
            writer.Put("key"u8.ToArray(), "uncommitted"u8.ToArray());
            await using var concurrent = await BeginWriteAsync(database);

            Assert.Null(await concurrent.GetAsync("key"u8.ToArray()));

            await writer.CommitAsync(WriteOptions(mode));
            Assert.Equal("uncommitted", await ReadAsync(database, "key"));
            Assert.Null(await concurrent.GetAsync("key"u8.ToArray()));
        });
    }

    [Fact]
    public async Task ShouldAllowDirtyWriteGivenUncommittedUpdateWhenSerialized()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await using var first = await BeginWriteAsync(database);
            first.Put("key"u8.ToArray(), "value1"u8.ToArray());
            await using var second = await BeginWriteAsync(database);
            second.Put("key"u8.ToArray(), "value2"u8.ToArray());

            await first.CommitAsync(WriteOptions(mode));
            await second.CommitAsync(WriteOptions(mode));

            Assert.Equal("value2", await ReadAsync(database, "key"));
        });
    }

    [Fact]
    public async Task ShouldReadUncommittedValueGivenPutInSameTransactionWhenReading()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await using var transaction = await BeginWriteAsync(database);
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());

            Assert.Equal("value", TestBytes.ToText((await transaction.GetAsync("key"u8.ToArray()))!.Value));
        });
    }

    [Fact]
    public async Task ShouldReadLatestCommittedValueGivenNewReaderAfterConcurrentWrite()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "key", "initial");
            await PutAsync(database, mode, "key", "updated");

            Assert.Equal("updated", await ReadAsync(database, "key"));
        });
    }

    [Fact]
    public async Task ShouldReturnOldValueGivenSnapshotBeforeWriteWhenReading()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "key", "v1");
            await using var snapshot = await BeginReadAsync(database);
            await PutAsync(database, mode, "key", "v2");

            Assert.Equal("v1", TestBytes.ToText((await snapshot.GetAsync("key"u8.ToArray()))!.Value));
            Assert.Equal("v2", await ReadAsync(database, "key"));
        });
    }

    [Fact]
    public async Task ShouldAllowCommitGivenReadKeyModifiedWhenConcurrentWrite()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "key", "initial");
            await using var observer = await BeginWriteAsync(database);
            Assert.Equal("initial", TestBytes.ToText((await observer.GetAsync("key"u8.ToArray()))!.Value));
            await PutAsync(database, mode, "key", "concurrent");

            await observer.CommitAsync(WriteOptions(mode));

            Assert.Equal("concurrent", await ReadAsync(database, "key"));
        });
    }

    [Fact]
    public async Task ShouldAllowPutCommitGivenReadKeyModifiedWhenConcurrentWrite()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "key", "initial");
            await using var writer = await BeginWriteAsync(database);
            await using (var reader = await BeginReadAsync(database))
            {
                Assert.Equal("initial", TestBytes.ToText((await reader.GetAsync("key"u8.ToArray()))!.Value));
            }

            await PutAsync(database, mode, "key", "concurrent");
            writer.Put("key"u8.ToArray(), "txn_value"u8.ToArray());
            await writer.CommitAsync(WriteOptions(mode));

            Assert.Equal("txn_value", await ReadAsync(database, "key"));
        });
    }

    [Fact]
    public async Task ShouldAllowConcurrentPutsGivenDifferentKeysWhenMultipleTransactions()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            var writers = await RunWorkersAsync(2, async index =>
            {
                var family = await database.ColumnFamilies.CreateAsync("test");
                await using var transaction = await database.Transactions.BeginAsync(
                    family,
                    PantsTransactionMode.ReadWrite);
                var key = index == 0 ? "key1" : "key2";
                var value = index == 0 ? "value1" : "value2";
                transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
                await transaction.CommitAsync(WriteOptions(mode));
                return index;
            });

            Assert.Equal(Enumerable.Range(0, 2), writers.Order());
            var family = Assert.IsAssignableFrom<IPantsColumnFamily>(await database.ColumnFamilies.GetAsync("test"));
            await using var reader = await database.Transactions.BeginAsync(
                family,
                PantsTransactionMode.ReadOnly);
            Assert.Equal("value1", TestBytes.ToText((await reader.GetAsync("key1"u8.ToArray()))!.Value));
            Assert.Equal("value2", TestBytes.ToText((await reader.GetAsync("key2"u8.ToArray()))!.Value));
        });
    }

    [Fact]
    public async Task ShouldRollbackAllOperationsGivenTransactionWhenAborted()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "key3", "existing");
            await using (var aborted = await BeginWriteAsync(database))
            {
                aborted.Put("key1"u8.ToArray(), "value1"u8.ToArray());
                aborted.Put("key2"u8.ToArray(), "value2"u8.ToArray());
                aborted.Delete("key3"u8.ToArray());
            }

            Assert.Null(await ReadAsync(database, "key1"));
            Assert.Null(await ReadAsync(database, "key2"));
            Assert.Equal("existing", await ReadAsync(database, "key3"));
        });
    }

    [Fact]
    public async Task ShouldReadLatestCommittedValueAfterMultipleUpdates()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "key", "initial");
            for (var index = 1; index <= 5; index++)
            {
                await PutAsync(database, mode, "key", $"v{index}");
            }

            Assert.Equal("v5", await ReadAsync(database, "key"));
        });
    }

    [Fact]
    public async Task ShouldMaintainIsolationUnderConcurrentTransactionPressureWhenStressed()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await RunWorkersAsync(20, async index =>
            {
                await PutAsync(database, mode, $"key{index}", $"value{index}");
                return index;
            });

            await using var reader = await BeginReadAsync(database);
            for (var index = 0; index < 20; index++)
            {
                Assert.Equal($"value{index}", TestBytes.ToText((await reader.GetAsync(
                    TestBytes.FromString($"key{index}")))!.Value));
            }
        });
    }

    [Fact]
    public async Task ShouldHandleHighConcurrencyReadersGivenManyTransactionsWhenActive()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            for (var index = 0; index < 10; index++)
            {
                await PutAsync(database, mode, $"key{index}", $"value{index}");
            }

            var observations = await RunWorkersAsync(50, async _ =>
            {
                var values = new string[10];
                for (var index = 0; index < values.Length; index++)
                {
                    values[index] = await ReadAsync(database, $"key{index}") ?? "<missing>";
                }

                return values;
            });

            Assert.Equal(50, observations.Length);
            foreach (var observation in observations)
            {
                Assert.Equal(Enumerable.Range(0, 10).Select(static index => $"value{index}"), observation);
            }
        });
    }

    [Fact]
    public async Task ShouldMaintainConsistencyWithMixedReaderWriterLoadWhenConcurrent()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var writers = RunWorkersAsync(10, async index =>
            {
                await start.Task;
                await PutAsync(database, mode, $"key{index}", $"value{index}");
                return index;
            });
            var readers = RunWorkersAsync(20, async _ =>
            {
                await start.Task;
                var observations = new (int Index, string? Value)[5];
                for (var index = 0; index < observations.Length; index++)
                {
                    observations[index] = (index, await ReadAsync(database, $"key{index}"));
                }

                return observations;
            });
            start.SetResult(true);

            await Task.WhenAll(writers, readers);

            foreach (var observation in await readers)
            {
                foreach (var (index, value) in observation)
                {
                    Assert.True(
                        value is null || value == $"value{index}",
                        $"Reader observed a torn or unexpected value for key{index}.");
                }
            }

            for (var index = 0; index < 10; index++)
            {
                Assert.Equal($"value{index}", await ReadAsync(database, $"key{index}"));
            }
        });
    }

    static async Task<T[]> RunWorkersAsync<T>(
        int workerCount,
        Func<int, Task<T>> worker)
    {
        var workers = Enumerable.Range(0, workerCount)
            .Select(index => Task.Run(() => worker(index)))
            .ToArray();
        return await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));
    }

    static async Task ForEachModeAsync(string[] modes, Func<string, string, Task> scenario)
    {
        foreach (var mode in modes)
        {
            using var directory = new TemporaryDirectory();
            await scenario(mode, directory.Path);
        }
    }

    static ValueTask<IPantsDatabase> OpenAsync(string mode, string path) => mode switch
    {
        "memory" => PantsDatabase.OpenAsync(PantsOpenOptions.InMemory()),
        "local" => PantsDatabase.OpenAsync(PantsOpenOptions.Local(path).WithBackgroundCompaction(false)),
        "cloud" => PantsDatabase.OpenAsync(
            PantsOpenOptions.SimulatedCloud(path, "pants-tests", "midge-isolation/")
                .WithBackgroundCompaction(false)),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown storage mode.")
    };

    static ValueTask<IPantsTransaction> BeginWriteAsync(IPantsDatabase database) =>
        database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);

    static ValueTask<IPantsTransaction> BeginReadAsync(IPantsDatabase database) =>
        database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);

    static PantsWriteOptions WriteOptions(string mode) =>
        mode == "cloud" ? PantsWriteOptions.CloudAsync : PantsWriteOptions.Buffered;

    static async Task PutAsync(IPantsDatabase database, string mode, string key, string value)
    {
        await using var transaction = await BeginWriteAsync(database);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(WriteOptions(mode));
    }

    static async Task<string?> ReadAsync(IPantsDatabase database, string key)
    {
        await using var reader = await BeginReadAsync(database);
        var value = await reader.GetAsync(TestBytes.FromString(key));
        return value is { } present ? TestBytes.ToText(present) : null;
    }
}
