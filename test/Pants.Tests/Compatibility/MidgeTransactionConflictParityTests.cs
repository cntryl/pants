using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeTransactionConflictParityTests
{
    static readonly string[] AllModes = ["memory", "local", "cloud"];
    static readonly string[] DurableModes = ["local", "cloud"];
    static readonly string?[] ConcurrentSameKeyValues = ["value1", "value2"];

    [Fact]
    public async Task ShouldAcceptBothCommittersGivenConcurrentPutsWhenLww()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            var completed = await CommitConcurrentlyAsync(database, mode, workerCount: 2,
                static (index, transaction) =>
                {
                    transaction.Put("key"u8.ToArray(), TestBytes.FromString($"value{index + 1}"));
                    return Task.CompletedTask;
                });

            Assert.Equal([0, 1], completed.Order());
            Assert.Contains(await ReadAsync(database, "key"), ConcurrentSameKeyValues);
        });
    }

    [Fact]
    public async Task ShouldAllowConcurrentDeletePutOperationsGivenLwwSemantics()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "key", "initial");
            await using var deleting = await BeginWriteAsync(database);
            await using var putting = await BeginWriteAsync(database);
            deleting.Delete("key"u8.ToArray());
            putting.Put("key"u8.ToArray(), "value"u8.ToArray());

            await deleting.CommitAsync(WriteOptions(mode));
            await putting.CommitAsync(WriteOptions(mode));

            Assert.Equal("value", await ReadAsync(database, "key"));
        });
    }

    [Fact]
    public async Task ShouldAllowConcurrentDeleteRangesGivenLwwSemantics()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "key1", "value1");
            await PutAsync(database, mode, "key2", "value2");

            await using (var first = await BeginWriteAsync(database))
            {
                first.DeleteRange("key1"u8.ToArray(), "key3"u8.ToArray());
                await first.CommitAsync(WriteOptions(mode));
            }

            await using (var second = await BeginWriteAsync(database))
            {
                second.DeleteRange("key1"u8.ToArray(), "key3"u8.ToArray());
                await second.CommitAsync(WriteOptions(mode));
            }

            Assert.Null(await ReadAsync(database, "key1"));
            Assert.Null(await ReadAsync(database, "key2"));
        });
    }

    [Fact]
    public async Task ShouldAllowConcurrentPutsToSameKeyGivenLwwSemantics()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await using var first = await BeginWriteAsync(database);
            await using var second = await BeginWriteAsync(database);
            first.Put("key"u8.ToArray(), "value1"u8.ToArray());
            second.Put("key"u8.ToArray(), "value2"u8.ToArray());

            await first.CommitAsync(WriteOptions(mode));
            await second.CommitAsync(WriteOptions(mode));

            Assert.Equal("value2", await ReadAsync(database, "key"));
        });
    }

    [Fact]
    public async Task ShouldAllowConcurrentWritesToDifferentKeys()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            var completed = await CommitConcurrentlyAsync(database, mode, workerCount: 10,
                static (index, transaction) =>
                {
                    transaction.Put(
                        TestBytes.FromString($"key{index}"),
                        TestBytes.FromString($"value{index}"));
                    return Task.CompletedTask;
                });

            Assert.Equal(Enumerable.Range(0, 10), completed.Order());
            for (var index = 0; index < 10; index++)
            {
                Assert.Equal($"value{index}", await ReadAsync(database, $"key{index}"));
            }
        });
    }

    [Fact]
    public async Task ShouldAllowDeleteRangeDeleteOperationsGivenLwwSemantics()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "key", "value");
            await using var pointDelete = await BeginWriteAsync(database);
            pointDelete.Delete("key"u8.ToArray());
            await using (var rangeDelete = await BeginWriteAsync(database))
            {
                rangeDelete.DeleteRange("key1"u8.ToArray(), "key3"u8.ToArray());
                await rangeDelete.CommitAsync(WriteOptions(mode));
            }

            await pointDelete.CommitAsync(WriteOptions(mode));

            Assert.Null(await ReadAsync(database, "key"));
        });
    }

    [Fact]
    public async Task ShouldAllowLostUpdateGivenPutReadModifyWriteWhenConcurrent()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "counter", "0");
            await using (var firstRead = await BeginReadAsync(database))
            {
                Assert.Equal("0", TestBytes.ToText((await firstRead.GetAsync("counter"u8.ToArray()))!.Value));
            }

            await using (var secondRead = await BeginReadAsync(database))
            {
                Assert.Equal("0", TestBytes.ToText((await secondRead.GetAsync("counter"u8.ToArray()))!.Value));
            }

            await PutAsync(database, mode, "counter", "1");
            await PutAsync(database, mode, "counter", "1");

            Assert.Equal("1", await ReadAsync(database, "counter"));
        });
    }

    [Fact]
    public async Task ShouldAllowOverlappingPutAfterDeleteRangeGivenLwwSemantics()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "key1", "value1");
            await PutAsync(database, mode, "key2", "value2");
            await using var stalePut = await BeginWriteAsync(database);
            stalePut.Put("key2"u8.ToArray(), "newvalue"u8.ToArray());
            await using (var rangeDelete = await BeginWriteAsync(database))
            {
                rangeDelete.DeleteRange("key1"u8.ToArray(), "key3"u8.ToArray());
                await rangeDelete.CommitAsync(WriteOptions(mode));
            }

            await stalePut.CommitAsync(WriteOptions(mode));

            Assert.Equal("newvalue", await ReadAsync(database, "key2"));
        });
    }

    [Fact]
    public async Task ShouldAllowPutThenDeleteRangeGivenLwwSemantics()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "key", "value");
            await using (var deleting = await BeginWriteAsync(database))
            {
                deleting.DeleteRange("key"u8.ToArray(), "keyz"u8.ToArray());
                await deleting.CommitAsync(WriteOptions(mode));
            }

            Assert.Null(await ReadAsync(database, "key"));
        });
    }

    [Fact]
    public async Task ShouldCommitNewKeyGivenCleanTransaction()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await using (var transaction = await BeginWriteAsync(database))
            {
                transaction.Put("newkey"u8.ToArray(), "newvalue"u8.ToArray());
                await transaction.CommitAsync(WriteOptions(mode));
            }

            Assert.Equal("newvalue", await ReadAsync(database, "newkey"));
        });
    }

    [Fact]
    public async Task ShouldCommitTransactionGivenConcurrentModificationsToDifferentKeys()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            var completed = await CommitConcurrentlyAsync(database, mode, workerCount: 2,
                static (index, transaction) =>
                {
                    var key = index == 0 ? "key1"u8.ToArray() : "key2"u8.ToArray();
                    var value = index == 0 ? "value1"u8.ToArray() : "value2"u8.ToArray();
                    transaction.Put(key, value);
                    return Task.CompletedTask;
                });

            Assert.Equal([0, 1], completed.Order());
            Assert.Equal("value1", await ReadAsync(database, "key1"));
            Assert.Equal("value2", await ReadAsync(database, "key2"));
        });
    }

    [Fact]
    public async Task ShouldCommitTransactionGivenNoConflicts()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await using (var transaction = await BeginWriteAsync(database))
            {
                transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
                await transaction.CommitAsync(WriteOptions(mode));
            }

            Assert.Equal("value", await ReadAsync(database, "key"));
        });
    }

    [Fact]
    public async Task ShouldDetectLostUpdateGivenCasPatternWhenValueChanged()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "counter", "0");
            await using var read = await BeginReadAsync(database);
            var original = await read.GetAsync("counter"u8.ToArray());
            Assert.Equal("0", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(original)));

            await using var stale = await BeginWriteAsync(database);
            stale.Put("counter"u8.ToArray(), "1"u8.ToArray());
            stale.AssertValue("counter"u8.ToArray(), original);
            await PutAsync(database, mode, "counter", "2");

            await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
                stale.CommitAsync(WriteOptions(mode)).AsTask());

            Assert.Equal("2", await ReadAsync(database, "counter"));
        });
    }

    [Fact]
    public async Task ShouldFinishConcurrentLocalSameKeyLwwBufferedCommitsWithinTimeout()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync("local", directory.Path);
        var completed = await CommitConcurrentlyAsync(
            database,
            "local",
            workerCount: 16,
            static (workerId, transaction) =>
            {
                transaction.Put(
                    "shared-key"u8.ToArray(),
                    TestBytes.FromString($"value-{workerId:00}"));
                return Task.CompletedTask;
            },
            completionTimeout: TimeSpan.FromSeconds(5));

        Assert.Equal(Enumerable.Range(0, 16), completed.Order());
        Assert.StartsWith("value-", Assert.IsType<string>(await ReadAsync(database, "shared-key")));
    }

    [Fact]
    public async Task ShouldHandleHighContentionWritesWithoutPanic()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await CommitConcurrentlyAsync(database, mode, workerCount: 8,
                static (index, transaction) =>
                {
                    transaction.Put("hotkey"u8.ToArray(), TestBytes.FromString($"value{index}"));
                    return Task.CompletedTask;
                });

            Assert.Contains(await ReadAsync(database, "hotkey"),
                Enumerable.Range(0, 8).Select(static index => $"value{index}"));
        });
    }

    [Fact]
    public async Task ShouldMaintainTransactionIsolationUnderStress()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "isolated", "initial");
            await using var snapshot = await BeginReadAsync(database);
            var completed = await CommitConcurrentlyAsync(database, mode, workerCount: 20,
                async (index, transaction) =>
                {
                    await using var read = await BeginReadAsync(database);
                    _ = await read.GetAsync("isolated"u8.ToArray());
                    transaction.Put("isolated"u8.ToArray(), TestBytes.FromString($"stress{index}"));
                });

            Assert.Equal(Enumerable.Range(0, 20), completed.Order());
            Assert.Equal("initial", TestBytes.ToText((await snapshot.GetAsync("isolated"u8.ToArray()))!.Value));
            Assert.NotEqual("initial", await ReadAsync(database, "isolated"));
        });
    }

    [Fact]
    public async Task ShouldOverwriteExistingValueGivenPutOnExistingKeyWhenCommitted()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "key", "existing");
            await PutAsync(database, mode, "key", "newvalue");

            Assert.Equal("newvalue", await ReadAsync(database, "key"));
        });
    }

    [Fact]
    public async Task ShouldPreserveBothUpdatesGivenNonOverlappingKeysWhenConcurrentCommits()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await using var first = await BeginWriteAsync(database);
            await using var second = await BeginWriteAsync(database);
            first.Put("key1"u8.ToArray(), "value1"u8.ToArray());
            second.Put("key2"u8.ToArray(), "value2"u8.ToArray());
            await first.CommitAsync(WriteOptions(mode));
            await second.CommitAsync(WriteOptions(mode));

            Assert.Equal("value1", await ReadAsync(database, "key1"));
            Assert.Equal("value2", await ReadAsync(database, "key2"));
        });
    }

    [Fact]
    public async Task ShouldPreserveBothWritesWhenNonOverlappingKeysGivenConcurrentCommits()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "key1", "old1");
            await PutAsync(database, mode, "key2", "old2");
            await using var first = await BeginWriteAsync(database);
            await using var second = await BeginWriteAsync(database);
            first.Put("key1"u8.ToArray(), "new1"u8.ToArray());
            second.Put("key2"u8.ToArray(), "new2"u8.ToArray());
            await first.CommitAsync(WriteOptions(mode));
            await second.CommitAsync(WriteOptions(mode));

            Assert.Equal("new1", await ReadAsync(database, "key1"));
            Assert.Equal("new2", await ReadAsync(database, "key2"));
        });
    }

    [Fact]
    public async Task ShouldReadValuesWithinTransaction()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "key", "value");
            await using var transaction = await BeginWriteAsync(database);

            Assert.Equal("value", TestBytes.ToText((await transaction.GetAsync("key"u8.ToArray()))!.Value));
        });
    }

    [Fact]
    public async Task ShouldRecoverConflictStateAfterEngineRestart()
    {
        await ForEachModeAsync(DurableModes, async (mode, path) =>
        {
            await using (var database = await OpenAsync(mode, path))
            {
                await PutAsync(database, mode, "conflict_key", "value1");
                await PutAsync(database, mode, "conflict_key", "value2");
                await database.ShutdownAsync(TimeSpan.FromSeconds(5));
            }

            await using var reopened = await OpenAsync(mode, path);
            Assert.Equal("value2", await ReadAsync(reopened, "conflict_key"));
        });
    }

    static async Task<int[]> CommitConcurrentlyAsync(
        IPantsDatabase database,
        string mode,
        int workerCount,
        Func<int, IPantsTransaction, Task> prepare,
        TimeSpan? completionTimeout = null)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var workers = Enumerable.Range(0, workerCount)
            .Select(async index =>
            {
                var reportedReady = false;
                try
                {
                    await using var transaction = await BeginWriteAsync(database);
                    await prepare(index, transaction);
                    reportedReady = true;
                    SignalReady();
                    await release.Task;
                    await transaction.CommitAsync(WriteOptions(mode));
                    return index;
                }
                finally
                {
                    if (!reportedReady)
                    {
                        SignalReady();
                    }
                }
            })
            .ToArray();

        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException exception)
        {
            release.TrySetResult(true);
            ObserveWorkerCompletion(Task.WhenAll(workers));
            throw new TimeoutException("Concurrent transaction workers did not reach the commit gate.", exception);
        }

        release.SetResult(true);
        var timeout = completionTimeout ?? TimeSpan.FromSeconds(30);
        var allWorkers = Task.WhenAll(workers);
        int[] completed;
        try
        {
            completed = await allWorkers.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            ObserveWorkerCompletion(allWorkers);
            throw;
        }

        Assert.Equal(Enumerable.Range(0, workerCount), completed.Order());
        return completed;

        void SignalReady()
        {
            if (Interlocked.Increment(ref readyCount) == workerCount)
            {
                ready.TrySetResult(true);
            }
        }
    }

    static void ObserveWorkerCompletion(Task workers) => _ = workers.ContinueWith(
        static completed => _ = completed.Exception,
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

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
            PantsOpenOptions.SimulatedCloud(path, "pants-tests", "midge-conflicts/")
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
