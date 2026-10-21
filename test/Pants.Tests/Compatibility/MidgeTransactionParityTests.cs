using System.Globalization;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeTransactionParityTests
{
    static readonly string[] AllModes = ["memory", "local", "cloud"];
    static readonly string[] DurableModes = ["local", "cloud"];

    [Fact]
    public async Task ShouldAllowLaterWriteToSameKeyGivenAbortedTransaction()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await using (var aborted = await BeginWriteAsync(database))
            {
                aborted.Put("key1"u8.ToArray(), "value1"u8.ToArray());
            }

            await PutAsync(database, mode, "key1", "value2");
            Assert.Equal("value2", await ReadAsync(database, "key1"));
        });
    }

    [Fact]
    public async Task ShouldPreserveReadOnlySnapshotGivenLaterCommittedWrite()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "key1", "v1");
            await using var snapshot = await BeginReadAsync(database);

            await PutAsync(database, mode, "key1", "v2");

            Assert.Equal("v1", TestBytes.ToText((await snapshot.GetAsync("key1"u8.ToArray()))!.Value));
            Assert.Equal("v2", await ReadAsync(database, "key1"));
        });
    }

    [Fact]
    public async Task ShouldMakeTransactionValuesVisibleGivenSuccessfulCommit()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "key_commit", "value_commit");

            Assert.Equal("value_commit", await ReadAsync(database, "key_commit"));
        });
    }

    [Fact]
    public async Task ShouldPersistCommittedTransactionGivenCleanShutdownWhenReopened()
    {
        await ForEachModeAsync(DurableModes, async (mode, path) =>
        {
            await using (var database = await OpenAsync(mode, path))
            {
                await PutAsync(database, mode, "key1", "value1");
            }

            await using var reopened = await OpenAsync(mode, path);
            Assert.Equal("value1", await ReadAsync(reopened, "key1"));
        });
    }

    [Fact]
    public async Task ShouldNotPersistAbortedTransactionGivenCleanShutdownWhenReopened()
    {
        await ForEachModeAsync(DurableModes, async (mode, path) =>
        {
            await using (var database = await OpenAsync(mode, path))
            {
                await using var aborted = await BeginWriteAsync(database);
                aborted.Put("key1"u8.ToArray(), "value1"u8.ToArray());
            }

            await using var reopened = await OpenAsync(mode, path);
            Assert.Null(await ReadAsync(reopened, "key1"));
        });
    }

    [Fact]
    public async Task ShouldRecoverAllCommittedTransactionValuesAfterCleanRestart()
    {
        await ForEachModeAsync(DurableModes, async (mode, path) =>
        {
            await using (var database = await OpenAsync(mode, path))
            {
                await using var transaction = await BeginWriteAsync(database);
                transaction.Put("tx_key1"u8.ToArray(), "tx_value1"u8.ToArray());
                transaction.Put("tx_key2"u8.ToArray(), "tx_value2"u8.ToArray());
                await transaction.CommitAsync(WriteOptions(mode));
            }

            await using var reopened = await OpenAsync(mode, path);
            await using var reader = await BeginReadAsync(reopened);
            Assert.Equal("tx_value1", TestBytes.ToText((await reader.GetAsync("tx_key1"u8.ToArray()))!.Value));
            Assert.Equal("tx_value2", TestBytes.ToText((await reader.GetAsync("tx_key2"u8.ToArray()))!.Value));
        });
    }

    [Fact]
    public async Task ShouldRecoverCommittedDeleteRangeGivenCleanRestart()
    {
        await ForEachModeAsync(DurableModes, async (mode, path) =>
        {
            await using (var database = await OpenAsync(mode, path))
            {
                for (var index = 0; index < 10; index++)
                {
                    await PutAsync(database, mode, $"key{index}", "initial_value");
                }
            }

            await using (var database = await OpenAsync(mode, path))
            {
                await using var deleting = await BeginWriteAsync(database);
                deleting.DeleteRange("key3"u8.ToArray(), "key7"u8.ToArray());
                await deleting.CommitAsync(WriteOptions(mode));
            }

            await using var reopened = await OpenAsync(mode, path);
            await using var reader = await BeginReadAsync(reopened);
            for (var index = 0; index < 10; index++)
            {
                var actual = await reader.GetAsync(TestBytes.FromString($"key{index}"));
                if (index is >= 3 and < 7)
                {
                    Assert.Null(actual);
                }
                else
                {
                    Assert.Equal("initial_value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(actual)));
                }
            }
        });
    }

    [Fact]
    public async Task ShouldAcceptConcurrentLastWriteWinsCommitsToOneKey()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "counter", "0");
            const int writerCount = 10;
            await RunConcurrentWorkersAsync(writerCount, async index =>
            {
                await using var read = await BeginReadAsync(database);
                _ = await read.GetAsync("counter"u8.ToArray());
                await using var transaction = await BeginWriteAsync(database);
                transaction.Put(
                    "counter"u8.ToArray(),
                    TestBytes.FromString(index.ToString(CultureInfo.InvariantCulture)));
                await transaction.CommitAsync(WriteOptions(mode));
            });

            Assert.Contains(await ReadAsync(database, "counter"),
                Enumerable.Range(0, writerCount).Select(static index =>
                    index.ToString(CultureInfo.InvariantCulture)));
        });
    }

    [Fact]
    public async Task ShouldPreserveACommittedValueGivenManyConcurrentLastWriteWinsWriters()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            const int writerCount = 50;
            await RunConcurrentWorkersAsync(writerCount, async index =>
            {
                await using var read = await BeginReadAsync(database);
                _ = await read.GetAsync("value"u8.ToArray());
                await using var transaction = await BeginWriteAsync(database);
                transaction.Put(
                    "value"u8.ToArray(),
                    TestBytes.FromString(index.ToString(CultureInfo.InvariantCulture)));
                await transaction.CommitAsync(WriteOptions(mode));
            });

            Assert.Contains(await ReadAsync(database, "value"),
                Enumerable.Range(0, writerCount).Select(static index =>
                    index.ToString(CultureInfo.InvariantCulture)));
        });
    }

    [Fact]
    public async Task ShouldPreserveFirstCommitGivenLaterTransactionDroppedWithoutCommit()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "key", "value1");
            await using (var later = await BeginWriteAsync(database))
            {
                later.Put("key"u8.ToArray(), "value2"u8.ToArray());
            }

            Assert.Equal("value1", await ReadAsync(database, "key"));
        });
    }

    [Fact]
    public async Task ShouldHandleTransactionAbortIdempotencyGivenMultipleRestartCycles()
    {
        await ForEachModeAsync(DurableModes, async (mode, path) =>
        {
            for (var cycle = 0; cycle < 3; cycle++)
            {
                await using (var database = await OpenAsync(mode, path))
                {
                    await using (var aborted = await BeginWriteAsync(database))
                    {
                        aborted.Put(
                            TestBytes.FromString($"cycle{cycle}_aborted_key"),
                            "should_never_persist"u8.ToArray());
                    }

                    await PutAsync(
                        database,
                        mode,
                        $"cycle{cycle}_key",
                        $"cycle{cycle}_value");
                }

                await using var reopened = await OpenAsync(mode, path);
                await using var reader = await BeginReadAsync(reopened);
                Assert.Equal(
                    $"cycle{cycle}_value",
                    TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                        await reader.GetAsync(TestBytes.FromString($"cycle{cycle}_key")))));
                Assert.Null(await reader.GetAsync(TestBytes.FromString($"cycle{cycle}_aborted_key")));

                for (var prior = 0; prior < cycle; prior++)
                {
                    Assert.Equal(
                        $"cycle{prior}_value",
                        TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                            await reader.GetAsync(TestBytes.FromString($"cycle{prior}_key")))));
                    Assert.Null(await reader.GetAsync(TestBytes.FromString($"cycle{prior}_aborted_key")));
                }
            }
        });
    }

    [Fact]
    public async Task ShouldPreserveLastCommittedValueGivenSequentialOverwritesAfterRestart()
    {
        await ForEachModeAsync(DurableModes, async (mode, path) =>
        {
            await using (var database = await OpenAsync(mode, path))
            {
                await PutAsync(database, mode, "counter", "0");
                await PutAsync(database, mode, "counter", "1");
                await PutAsync(database, mode, "counter", "2");
            }

            await using var reopened = await OpenAsync(mode, path);
            Assert.Equal("2", await ReadAsync(reopened, "counter"));
        });
    }

    [Fact]
    public async Task ShouldPreserveAllValuesGivenLargeInMemoryTransaction()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using (var transaction = await BeginWriteAsync(database))
        {
            for (var index = 0; index < 500; index++)
            {
                transaction.Put(TestBytes.FromString($"mem_only_{index:0000}"), "value"u8.ToArray());
            }

            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using var reader = await BeginReadAsync(database);
        for (var index = 0; index < 500; index++)
        {
            Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync(TestBytes.FromString($"mem_only_{index:0000}")))));
        }
    }

    [Fact]
    public async Task ShouldPreserveEveryMemoryValueWhenThreadsWriteConcurrently()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await RunConcurrentWritesAsync(database, threadCount: 4, operationsPerThread: 50,
            (threadId, operationId) => ($"key_{threadId}_{operationId}", $"val_{threadId}_{operationId}"));

        var expected = from threadId in Enumerable.Range(0, 4)
                       from operationId in Enumerable.Range(0, 50)
                       select ($"key_{threadId}_{operationId}", $"val_{threadId}_{operationId}");
        await AssertAllValuesAndScanAsync(database, expected.ToArray());
    }

    [Fact]
    public async Task ShouldPreserveEveryWalBackedValueWhenThreadsWriteConcurrently()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        await RunConcurrentWritesAsync(database, threadCount: 8, operationsPerThread: 500,
            (threadId, operationId) =>
                ($"key-t{threadId:00}-o{operationId:000000}", $"val-{operationId}"));

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(metrics.WalAppendCount > 0);
        var expected = from threadId in Enumerable.Range(0, 8)
                       from operationId in Enumerable.Range(0, 500)
                       select ($"key-t{threadId:00}-o{operationId:000000}", $"val-{operationId}");
        await AssertAllValuesAndScanAsync(database, expected.ToArray());
    }

    [Fact]
    public async Task ShouldPreserveLastCommittedValueGivenSequentialOverwrites()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        for (var index = 0; index < 100; index++)
        {
            await PutAsync(
                database,
                "memory",
                "counter",
                index.ToString(CultureInfo.InvariantCulture));
        }

        Assert.Equal("99", await ReadAsync(database, "counter"));
    }

    static async Task RunConcurrentWritesAsync(
        IPantsDatabase database,
        int threadCount,
        int operationsPerThread,
        Func<int, int, (string Key, string Value)> makeEntry) =>
        await RunConcurrentWorkersAsync(threadCount, async threadId =>
        {
            for (var operationId = 0; operationId < operationsPerThread; operationId++)
            {
                var (key, value) = makeEntry(threadId, operationId);
                await PutAsync(database, "local", key, value);
            }
        });

    static async Task RunConcurrentWorkersAsync(int workerCount, Func<int, Task> worker)
    {
        var readyWorkers = 0;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, workerCount)
            .Select(async workerId =>
            {
                await Task.Yield();
                if (Interlocked.Increment(ref readyWorkers) == workerCount)
                {
                    ready.TrySetResult(true);
                }
                await start.Task;
                await worker(workerId);
            })
            .ToArray();

        await ready.Task.WaitAsync(TestTimeouts.Expected);
        start.SetResult(true);
        await Task.WhenAll(workers).WaitAsync(TestTimeouts.Expected);
    }

    static async Task AssertAllValuesAndScanAsync(
        IPantsDatabase database,
        (string Key, string Value)[] expected)
    {
        await using var reader = await BeginReadAsync(database);
        foreach (var (key, value) in expected)
        {
            Assert.Equal(value, TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync(TestBytes.FromString(key)))));
        }

        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var entries = await CollectAsync(scan);
        Assert.Equal(expected.Length, entries.Count);
        var keys = entries.Select(static entry => TestBytes.ToText(entry.Key)).ToArray();
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
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
            PantsOpenOptions.SimulatedCloud(path, "pants-tests", "midge-transactions/")
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
