using Cntryl.Pants.Scan;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeTransactionSnapshotTrackingParityTests
{
    static readonly string[] AllModes = ["memory", "local", "cloud"];
    static readonly string[] DurableModes = ["local", "cloud"];
    static readonly string[] GcGenerations = ["new1", "new2"];

    [Fact]
    public async Task ShouldKeepSnapshotRangeScanStableWhenCompactionRunsConcurrently()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            var compactionGate = mode == "memory"
                ? null
                : new BlockingCompactionOutputFailpointHandler();
            await using var database = compactionGate is null
                ? await OpenAsync(mode, path)
                : await PantsDatabase.OpenForTestingAsync(
                    OpenOptions(mode, path),
                    new RuntimeDependencies(compactionGate));
            var family = await database.ColumnFamilies.CreateAsync("concurrent-snapshot");
            var initialGenerations = mode == "memory" ? 1 : 4;
            for (var generation = 0; generation < initialGenerations; generation++)
            {
                await PutRowsAsync(database, mode, family, "concurrent_key_", 32, "baseline");
                await database.Maintenance.FlushAsync(family);
            }

            await using var snapshot = await BeginReadAsync(database, family);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var compactionWorker = Task.Run(async () =>
            {
                await start.Task;
                for (var round = 0; round < 5; round++)
                {
                    await PutRowsAsync(
                        database,
                        mode,
                        family,
                        "concurrent_key_",
                        32,
                        $"round_{round}");
                    await database.Maintenance.FlushAsync(family);
                    await database.Maintenance.CompactAllAsync();
                }
            });

            start.SetResult();
            if (compactionGate is not null)
            {
                try
                {
                    await compactionGate.WaitUntilBlockedAsync(TestTimeouts.Expected);
                    Assert.False(compactionWorker.IsCompleted);
                    await AssertSnapshotRowsAsync(snapshot, "concurrent_key_", 32, "baseline");
                }
                finally
                {
                    compactionGate.Release();
                }
            }
            else
            {
                var scansWhileWorkerRunning = 0;
                while (!compactionWorker.IsCompleted)
                {
                    await AssertSnapshotRowsAsync(snapshot, "concurrent_key_", 32, "baseline");
                    scansWhileWorkerRunning++;
                    await Task.Yield();
                }

                Assert.True(scansWhileWorkerRunning > 0, $"mode: {mode}");
            }

            await compactionWorker.WaitAsync(TestTimeouts.Expected);
            await AssertSnapshotRowsAsync(snapshot, "concurrent_key_", 32, "baseline");
            Assert.Equal(1, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
        });
    }

    [Fact]
    public async Task ShouldNotRegisterSnapshotGivenDroppedCfWhenBeginTxFails()
    {
        await ForEachModeAsync(DurableModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            var family = await database.ColumnFamilies.CreateAsync("dropped-snapshot");
            await PutRowsAsync(database, mode, family, "seed-", 2, "value");
            await database.Maintenance.FlushAsync(family);
            await database.ColumnFamilies.DropAsync(family);

            foreach (var transactionMode in new[]
                     {
                         PantsTransactionMode.ReadOnly,
                         PantsTransactionMode.ReadWrite
                     })
            {
                var error = await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
                    database.Transactions.BeginAsync(family, transactionMode).AsTask());
                Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
                var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
                Assert.Equal(0, metrics.ActiveSnapshots);
                Assert.Equal(0, metrics.PinnedSsts);
            }
        });
    }

    [Fact]
    public async Task ShouldPreserveIteratorViewWhenFlushAndCompactionRunDuringActiveScan()
    {
        using var directory = new TemporaryDirectory();
        var options = LocalOptions(directory.Path);
        await using var database = await PantsDatabase.OpenAsync(options);
        var family = await database.ColumnFamilies.CreateAsync("iterator-snapshot");
        for (var generation = 0; generation < 4; generation++)
        {
            await PutRowsAsync(database, "local", family, "row-", 27, "baseline");
            await database.Maintenance.FlushAsync(family);
        }

        var inputs = (await database.Diagnostics.GetStorageLayoutAsync())
            .Levels
            .SelectMany(static level => level.Files)
            .Select(static file => file.Name)
            .ToArray();
        Assert.Equal(4, inputs.Length);
        await using var reader = await BeginReadAsync(database, family);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var iterator = scan.GetAsyncEnumerator();

        try
        {
            Assert.True(await iterator.MoveNextAsync());
            Assert.Equal("row-000", TestBytes.ToText(iterator.Current.Key));
            Assert.Equal("baseline", TestBytes.ToText(iterator.Current.Value));

            await reader.RollbackAsync();
            await PutRowsAsync(database, "local", family, "row-", 24, "updated");
            await database.Maintenance.FlushAsync(family);
            await database.Maintenance.CompactAllAsync();

            var layout = await database.Diagnostics.GetStorageLayoutAsync();
            Assert.Contains(layout.Levels, static level => level.Level == 1 && level.FileCount > 0);
            var liveNames = layout.Levels
                .SelectMany(static level => level.Files)
                .Select(static file => file.Name)
                .ToHashSet(StringComparer.Ordinal);
            var retiredInputs = inputs.Where(name => !liveNames.Contains(name)).ToArray();
            Assert.NotEmpty(retiredInputs);
            Assert.All(retiredInputs, name =>
                Assert.True(File.Exists(Path.Combine(directory.Path, "sst", name)), name));
            Assert.True((await database.Diagnostics.GetRuntimeMetricsAsync()).PinnedSsts > 0);

            var remaining = new List<PantsEntry>();
            while (await iterator.MoveNextAsync())
            {
                remaining.Add(iterator.Current);
            }

            Assert.Equal(26, remaining.Count);
            Assert.All(remaining, entry =>
            {
                Assert.StartsWith("row-", TestBytes.ToText(entry.Key), StringComparison.Ordinal);
                Assert.Equal("baseline", TestBytes.ToText(entry.Value));
            });

            await iterator.DisposeAsync();
            await scan.DisposeAsync();
            Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).PinnedSsts);
        }
        finally
        {
            await iterator.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShouldPreserveSnapshotRangeScanWhenCompactionGcRunsWithSnapshotActive()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            var family = await database.ColumnFamilies.CreateAsync("gc-snapshot");
            await PutRowsAsync(database, mode, family, "gc_key_", 64, "old");
            await database.Maintenance.FlushAsync(family);
            await using var snapshot = await BeginReadAsync(database, family);

            foreach (var generation in GcGenerations)
            {
                await PutRowsAsync(database, mode, family, "gc_key_", 64, generation);
                await database.Maintenance.FlushAsync(family);
                await database.Maintenance.CompactAllAsync();
            }

            await AssertSnapshotRowsAsync(snapshot, "gc_key_", 64, "old");
            await using var current = await BeginReadAsync(database, family);
            var currentValue = await current.GetAsync("gc_key_000"u8.ToArray());
            Assert.NotNull(currentValue);
            Assert.Equal("new2", TestBytes.ToText(currentValue.Value));
        });
    }

    [Fact]
    public async Task ShouldPreserveSnapshotValueWhenDeleteIsCompactedWithSnapshotActive()
    {
        await ForEachModeAsync(DurableModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            var family = await database.ColumnFamilies.CreateAsync("delete-snapshot");
            await PutAsync(database, mode, family, "k", "v1");
            await database.Maintenance.FlushAsync(family);
            await using var snapshot = await BeginReadAsync(database, family);

            await using (var deleting = await BeginWriteAsync(database, family))
            {
                deleting.Delete("k"u8.ToArray());
                await deleting.CommitAsync(WriteOptions(mode));
            }

            await database.Maintenance.FlushAsync(family);
            for (var index = 0; index < 2; index++)
            {
                await PutAsync(database, mode, family, $"filler-{index}", "value");
                await database.Maintenance.FlushAsync(family);
            }

            var before = await database.Diagnostics.GetStorageLayoutAsync();
            Assert.Equal(4, before.Levels.Single(static level => level.Level == 0).FileCount);
            await database.Maintenance.CompactAllAsync();
            var after = await database.Diagnostics.GetStorageLayoutAsync();
            Assert.Contains(after.Levels, static level => level.Level == 1 && level.FileCount > 0);
            Assert.True((after.Levels.SingleOrDefault(static level => level.Level == 0)?.FileCount ?? 0) < 4);
            Assert.Equal("v1", TestBytes.ToText((await snapshot.GetAsync("k"u8.ToArray()))!.Value));

            await using var current = await BeginReadAsync(database, family);
            Assert.Null(await current.GetAsync("k"u8.ToArray()));
        });
    }

    [Fact]
    public async Task ShouldRegisterSnapshotWhenBeginTxStartsTransaction()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            var family = await database.ColumnFamilies.CreateAsync("register-snapshot");
            Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);

            var transaction = await BeginReadAsync(database, family);
            Assert.Equal(1, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
            await transaction.DisposeAsync();
            Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
        });
    }

    [Fact]
    public async Task ShouldReportActiveSnapshotImmediatelyWhenBeginTxReturns()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            var family = await database.ColumnFamilies.CreateAsync("immediate-snapshot");
            var transaction = await BeginReadAsync(database, family);

            Assert.Equal(1, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
            await transaction.DisposeAsync();
            Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
        });
    }

    [Fact]
    public async Task ShouldReportSnapshotRetentionPressureMetricsWhenSnapshotPinsSsts()
    {
        await ForEachModeAsync(DurableModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            var family = await database.ColumnFamilies.CreateAsync("retention-metrics");
            await PutRowsAsync(database, mode, family, "metric_key_", 32, "v");
            await database.Maintenance.FlushAsync(family);
            await using var snapshot = await BeginReadAsync(database, family);

            var immediateMetrics = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(1, immediateMetrics.ActiveSnapshots);
            Assert.InRange(immediateMetrics.OldestSnapshotAgeSeconds, 0, 1);

            for (var generation = 0; generation < 2; generation++)
            {
                await PutRowsAsync(database, mode, family, "metric_key_", 32, $"new-{generation}");
                await database.Maintenance.FlushAsync(family);
            }

            await database.Maintenance.CompactAllAsync();
            Assert.True((await database.Diagnostics.GetRuntimeMetricsAsync()).PinnedSsts > 0);

            await snapshot.DisposeAsync();
            Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).PinnedSsts);
        });
    }

    [Fact]
    public async Task ShouldReturnBusyWhenScanIteratorIsStillOpenAcrossShutdownCall()
    {
        using var directory = new TemporaryDirectory();
        var options = LocalOptions(directory.Path);
        var database = await PantsDatabase.OpenAsync(options);
        var family = await database.ColumnFamilies.CreateAsync("shutdown-scan");
        await PutAsync(database, "local", family, "key", "value");
        var reader = await BeginReadAsync(database, family);
        var scan = await reader.ScanAsync(new PantsScanQuery());

        try
        {
            var busyTransaction = await Assert.ThrowsAsync<PantsBusyException>(() =>
                database.ShutdownAsync(TimeSpan.FromMilliseconds(100)).AsTask());
            Assert.Equal(PantsErrorCode.Busy, busyTransaction.Code);

            await reader.RollbackAsync();
            var busyScan = await Assert.ThrowsAsync<PantsBusyException>(() =>
                database.ShutdownAsync(TimeSpan.FromMilliseconds(100)).AsTask());
            Assert.Equal(PantsErrorCode.Busy, busyScan.Code);

            await scan.DisposeAsync();
            await database.ShutdownAsync(TestTimeouts.Expected);
        }
        finally
        {
            await scan.DisposeAsync();
            await reader.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShouldReturnStableResultsWhenScanningAfterColumnFamilyDroppedMidTransaction()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(LocalOptions(directory.Path));
        var family = await database.ColumnFamilies.CreateAsync("drop-scan");
        await using (var seed = await BeginWriteAsync(database, family))
        {
            foreach (var key in new[] { "a", "b", "c" })
            {
                seed.Put(TestBytes.FromString(key), "value"u8.ToArray());
            }

            await seed.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.Maintenance.FlushAsync(family);
        await using var reader = await BeginReadAsync(database, family);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var iterator = scan.GetAsyncEnumerator();

        try
        {
            Assert.True(await iterator.MoveNextAsync());
            Assert.Equal("a", TestBytes.ToText(iterator.Current.Key));
            await database.ColumnFamilies.DropAsync(family);

            var remaining = new List<string>();
            while (await iterator.MoveNextAsync())
            {
                remaining.Add(TestBytes.ToText(iterator.Current.Key));
            }

            Assert.Equal(["b", "c"], remaining);
            var beginError = await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
                BeginReadAsync(database, family).AsTask());
            Assert.Equal(PantsErrorCode.InvalidArgument, beginError.Code);
        }
        finally
        {
            await iterator.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShouldShadowNeitherWriteWhenDeleteRangeTombstonePrecedesNewerPutAcrossSstGenerations()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(LocalOptions(directory.Path));
        var family = await database.ColumnFamilies.CreateAsync("range-generations");
        await PutAsync(database, "local", family, "middle", "old");
        await database.Maintenance.FlushAsync(family);

        await using (var deleting = await BeginWriteAsync(database, family))
        {
            deleting.DeleteRange("a"u8.ToArray(), "z"u8.ToArray());
            await deleting.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.Maintenance.FlushAsync(family);
        await PutAsync(database, "local", family, "middle", "new");
        await database.Maintenance.FlushAsync(family);
        await PutAsync(database, "local", family, "zz", "filler");
        await database.Maintenance.FlushAsync(family);
        var before = await database.Diagnostics.GetStorageLayoutAsync();
        Assert.Equal(4, before.Levels.Single(static level => level.Level == 0).FileCount);

        await database.Maintenance.CompactAllAsync();
        await using var reader = await BeginReadAsync(database, family);
        Assert.Equal("new", TestBytes.ToText((await reader.GetAsync("middle"u8.ToArray()))!.Value));
        foreach (var direction in new[] { PantsScanDirection.Forward, PantsScanDirection.Reverse })
        {
            var rows = await CollectAsync(await reader.ScanAsync(new PantsScanQuery { Direction = direction }));
            Assert.Contains(rows, entry =>
                TestBytes.ToText(entry.Key) == "middle" && TestBytes.ToText(entry.Value) == "new");
        }

        var after = await database.Diagnostics.GetStorageLayoutAsync();
        Assert.Contains(after.Levels, static level => level.Level == 1 && level.FileCount > 0);
    }

    [Fact]
    public async Task ShouldUnregisterSnapshotWhenCommitFinishesTransaction()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            var family = await database.ColumnFamilies.CreateAsync("commit-snapshot");
            await using var transaction = await BeginWriteAsync(database, family);
            transaction.Put("k"u8.ToArray(), "v"u8.ToArray());
            Assert.Equal(1, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
            await transaction.CommitAsync(WriteOptions(mode));
            Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
        });
    }

    [Fact]
    public async Task ShouldUnregisterSnapshotWhenDropEndsTransaction()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            var family = await database.ColumnFamilies.CreateAsync("drop-snapshot");
            var transaction = await BeginReadAsync(database, family);
            Assert.Equal(1, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
            await transaction.DisposeAsync();
            Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
        });
    }

    [Fact]
    public async Task ShouldUnregisterSnapshotWhenRollbackEndsTransaction()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            var family = await database.ColumnFamilies.CreateAsync("rollback-snapshot");
            await using var transaction = await BeginReadAsync(database, family);
            Assert.Equal(1, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
            await transaction.RollbackAsync();
            Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
        });
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
        _ => PantsDatabase.OpenAsync(OpenOptions(mode, path))
    };

    static PantsOpenOptions OpenOptions(string mode, string path) => mode switch
    {
        "local" => LocalOptions(path),
        "cloud" => PantsOpenOptions.SimulatedCloud(path, "pants-tests", "midge-snapshot-tracking/")
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: 100,
                BackgroundEnabled: false)),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown persistent storage mode.")
    };

    static PantsOpenOptions LocalOptions(string path) => PantsOpenOptions.Local(path)
        .WithBackgroundCompaction(false)
        .WithCompaction(new PantsCompactionConfiguration(
            L0FileCountTrigger: 100,
            BackgroundEnabled: false));

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
        string mode,
        IPantsColumnFamily family,
        string key,
        string value)
    {
        await using var transaction = await BeginWriteAsync(database, family);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(WriteOptions(mode));
    }

    static async Task PutRowsAsync(
        IPantsDatabase database,
        string mode,
        IPantsColumnFamily family,
        string keyPrefix,
        int count,
        string value)
    {
        await using var transaction = await BeginWriteAsync(database, family);
        for (var index = 0; index < count; index++)
        {
            transaction.Put(
                TestBytes.FromString($"{keyPrefix}{index:000}"),
                TestBytes.FromString(value));
        }

        await transaction.CommitAsync(WriteOptions(mode));
    }

    static async Task AssertSnapshotRowsAsync(
        IPantsTransaction snapshot,
        string keyPrefix,
        int count,
        string expectedValue)
    {
        var rows = await CollectAsync(await snapshot.ScanAsync(new PantsScanQuery()));
        Assert.Equal(count, rows.Count);
        Assert.All(rows, entry =>
        {
            Assert.StartsWith(keyPrefix, TestBytes.ToText(entry.Key), StringComparison.Ordinal);
            Assert.Equal(expectedValue, TestBytes.ToText(entry.Value));
        });
    }

    static async Task<List<PantsEntry>> CollectAsync(IPantsScan scan)
    {
        await using (scan)
        {
            var entries = new List<PantsEntry>();
            await foreach (var entry in scan)
            {
                entries.Add(entry);
            }

            return entries;
        }
    }
}
