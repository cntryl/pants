using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeQueryBoundaryBehaviorTests
{
    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldReturnExactOrderedRowsForBoundsDirectionsLimitsAndFreshScans(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        Assert.Empty(await ScanAsync(database, new PantsScanQuery()));
        Assert.Empty(await ScanAsync(database, new PantsScanQuery
        {
            StartInclusive = Key(0),
            EndExclusive = Key(20)
        }));
        await WriteAsync(database, mode, transaction =>
        {
            for (var index = 0; index < 10; index++)
            {
                transaction.Put(Key(index * 2), Value(index * 2));
            }
        });

        var expected = Enumerable.Range(0, 10).Select(index => Row(index * 2)).ToArray();
        Assert.Equal(expected, await ScanAsync(database, new PantsScanQuery()));
        Assert.Equal(expected, await ScanAsync(database, new PantsScanQuery()));
        Assert.Equal(expected, await ScanAsync(database, new PantsScanQuery()));
        Assert.Equal(expected, await ScanAsync(database, new PantsScanQuery
        {
            StartInclusive = Key(0),
            EndExclusive = Key(99)
        }));
        Assert.Equal(expected.Reverse(), await ScanAsync(database, new PantsScanQuery
        {
            Direction = PantsScanDirection.Reverse
        }));
        Assert.Equal(expected.Take(3), await ScanAsync(database, new PantsScanQuery { Limit = 3 }));
        Assert.Equal(expected.Reverse().Take(3), await ScanAsync(database, new PantsScanQuery
        {
            Direction = PantsScanDirection.Reverse,
            Limit = 3
        }));
        var bounded = new PantsScanQuery { StartInclusive = Key(3), EndExclusive = Key(12) };
        var boundedExpected = expected.Where(row => StringComparer.Ordinal.Compare(row.Key, "key-0003") >= 0 &&
            StringComparer.Ordinal.Compare(row.Key, "key-0012") < 0).ToArray();
        Assert.Equal(boundedExpected, await ScanAsync(database, bounded));
        Assert.Equal(boundedExpected.Reverse(), await ScanAsync(database, bounded with
        {
            Direction = PantsScanDirection.Reverse
        }));
        Assert.Equal(boundedExpected.Take(2), await ScanAsync(database, bounded with { Limit = 2 }));
        Assert.Equal(boundedExpected.Reverse(), await ScanAsync(database, bounded with
        {
            StartInclusive = Key(4),
            Direction = PantsScanDirection.Reverse
        }));
        Assert.Empty(await ScanAsync(database, new PantsScanQuery
        {
            StartInclusive = Key(4),
            EndExclusive = Key(4)
        }));
        Assert.Empty(await ScanAsync(database, new PantsScanQuery { StartInclusive = Key(99) }));
        Assert.Empty(await ScanAsync(database, new PantsScanQuery
        {
            StartInclusive = Key(99),
            EndExclusive = Key(99)
        }));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldMergeCommittedPointAndRangeTombstonesWithExactSurvivors(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        await WriteAsync(database, mode, transaction =>
        {
            for (var index = 0; index < 20; index++)
            {
                transaction.Put(Key(index), Value(index));
            }
        });
        await WriteAsync(database, mode, transaction =>
        {
            transaction.Delete(Key(1));
            transaction.DeleteRange(Key(3), Key(10));
        });
        await WriteAsync(database, mode, transaction => transaction.DeleteRange(Key(15), Key(18)));
        await WriteAsync(database, mode, transaction => transaction.Put(Key(5), "replacement"u8.ToArray()));
        var expected = Enumerable.Range(0, 20)
            .Where(index => index != 1 && (index < 3 || index >= 10) && (index < 15 || index >= 18) || index == 5)
            .Select(index => index == 5 ? (Key: "key-0005", Value: "replacement") : Row(index)).ToArray();
        await AssertPointsAsync(database, expected, 20);
        Assert.Equal(expected, await ScanAsync(database, new PantsScanQuery()));
        Assert.Equal(expected.Reverse(), await ScanAsync(database, new PantsScanQuery
        {
            Direction = PantsScanDirection.Reverse
        }));
        await WriteAsync(database, mode, transaction => transaction.Delete(Key(5)));
        Assert.Equal(expected.Where(row => row.Key != "key-0005"), await ScanAsync(database, new PantsScanQuery()));
        await WriteAsync(database, mode, transaction => transaction.DeleteRange(Key(18), Key(19)));
        var singleDeleted = expected.Where(row => row.Key != "key-0005" && row.Key != "key-0018").ToArray();
        await AssertPointsAsync(database, singleDeleted, 20);
        Assert.Equal(singleDeleted, await ScanAsync(database, new PantsScanQuery()));
        await WriteAsync(database, mode, transaction => transaction.Put(Key(5), "third"u8.ToArray()));
        var final = singleDeleted.Append((Key: "key-0005", Value: "third"))
            .OrderBy(row => row.Key, StringComparer.Ordinal).ToArray();
        Assert.Equal(final, await ScanAsync(database, new PantsScanQuery()));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldApplyConcurrentDisjointRangesAndPreserveEveryOtherExactValue(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        await WriteAsync(database, mode, transaction =>
        {
            for (var index = 0; index < 100; index++)
            {
                transaction.Put(Key(index), Value(index));
            }
        });
        var writers = new List<IPantsTransaction>();
        try
        {
            for (var index = 0; index < 5; index++)
            {
                var writer = await database.Transactions.BeginAsync(
                    database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
                writers.Add(writer);
                writer.DeleteRange(Key(index * 10), Key((index + 1) * 10));
            }

            await Task.WhenAll(writers.Select(writer => writer.CommitAsync(
                StorageModeTestHarness.GetWriteOptions(mode)).AsTask()));
        }
        finally
        {
            foreach (var writer in writers)
            {
                await writer.DisposeAsync();
            }
        }

        var expected = Enumerable.Range(50, 50).Select(Row).ToArray();
        await AssertPointsAsync(database, expected, 100);
        Assert.Equal(expected, await ScanAsync(database, new PantsScanQuery()));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldReturnEveryLargeResultForFourConcurrentIndependentScans(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        await WriteAsync(database, mode, transaction =>
        {
            for (var index = 0; index < 1000; index++)
            {
                transaction.Put(Key(index), Value(index));
            }
        });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = 0;
        var tasks = Enumerable.Range(0, 4).Select(async index =>
        {
            await using var reader = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
            await using var scan = await reader.ScanAsync(new PantsScanQuery
            {
                Direction = index % 2 == 0 ? PantsScanDirection.Forward : PantsScanDirection.Reverse
            });
            if (Interlocked.Increment(ref admitted) == 4)
            {
                ready.TrySetResult();
            }
            await ready.Task.WaitAsync(TestTimeouts.Expected);
            var rows = new List<(string Key, string Value)>();
            await foreach (var entry in scan)
            {
                rows.Add((TestBytes.ToText(entry.Key), TestBytes.ToText(entry.Value)));
            }
            var expected = Enumerable.Range(0, 1000).Select(Row);
            Assert.Equal(index % 2 == 0 ? expected : expected.Reverse(), rows);
        });
        await Task.WhenAll(tasks);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldPreserveExclusiveRangeEndpointAndExactSurvivorsAfterReopen(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path))
        {
            await WriteAsync(database, mode, transaction =>
            {
                for (var index = 0; index < 10; index++)
                {
                    transaction.Put(Key(index), Value(index));
                }
            });
            await WriteAsync(database, mode, transaction => transaction.DeleteRange(Key(2), Key(8)));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        await using var reopened = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        var expected = Enumerable.Range(0, 10).Where(index => index < 2 || index >= 8).Select(Row).ToArray();
        await AssertPointsAsync(reopened, expected, 10);
        Assert.Equal(expected, await ScanAsync(reopened, new PantsScanQuery()));
    }

    [Theory]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldMergeThreeSstsAndTheMemtableThenScanTheCompactedGeneration(string mode)
    {
        using var directory = new TemporaryDirectory();
        var options = (mode == "local" ? PantsOpenOptions.Local(directory.Path) :
            PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "query-generations/"))
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenAsync(options);
        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (var generation = 0; generation < 4; generation++)
        {
            var start = generation == 0 ? 0 : generation == 1 ? 5 : generation == 2 ? 15 : 20;
            var end = generation < 2 ? start + 10 : start + 5;
            await using (var writer = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
            {
                for (var index = start; index < end; index++)
                {
                    var value = $"generation-{generation}/{index}";
                    writer.Put(Key(index), TestBytes.FromString(value));
                    expected[$"key-{index:0000}"] = value;
                }
                await writer.CommitAsync(StorageModeTestHarness.GetWriteOptions(mode));
            }
            if (generation < 3)
            {
                await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            }
        }
        Assert.Equal(3, (await database.Diagnostics.GetStorageLayoutAsync()).Levels.Sum(level => level.FileCount));
        var original = expected.Select(pair => (Key: pair.Key, Value: pair.Value)).ToArray();
        Assert.Equal(original, await ScanAsync(database, new PantsScanQuery()));
        Assert.Equal(original.Reverse(), await ScanAsync(database, new PantsScanQuery
        {
            Direction = PantsScanDirection.Reverse
        }));
        await using var pinned = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        await WriteAsync(database, mode, writer =>
        {
            writer.Delete(Key(0));
            writer.DeleteRange(Key(2), Key(5));
            writer.Put(Key(10), "compacted-latest"u8.ToArray());
        });
        expected.Remove("key-0000");
        for (var index = 2; index < 5; index++)
        {
            expected.Remove($"key-{index:0000}");
        }
        expected["key-0010"] = "compacted-latest";
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var inputNames = (await database.Diagnostics.GetStorageLayoutAsync()).Levels
            .SelectMany(level => level.Files).Select(file => file.Name).ToArray();
        Assert.Equal(4, inputNames.Length);
        await database.Maintenance.CompactAllAsync();
        var outputNames = (await database.Diagnostics.GetStorageLayoutAsync()).Levels
            .SelectMany(level => level.Files).Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(inputNames, outputNames.Contains);
        var compacted = expected.Select(pair => (Key: pair.Key, Value: pair.Value)).ToArray();
        Assert.Equal(compacted, await ScanAsync(database, new PantsScanQuery()));
        Assert.Equal(compacted.Reverse(), await ScanAsync(database, new PantsScanQuery
        {
            Direction = PantsScanDirection.Reverse
        }));
        await using var oldScan = await pinned.ScanAsync(new PantsScanQuery());
        var retained = new List<(string Key, string Value)>();
        await foreach (var entry in oldScan)
        {
            retained.Add((TestBytes.ToText(entry.Key), TestBytes.ToText(entry.Value)));
        }
        Assert.Equal(original, retained);
    }

    static byte[] Key(int index) => TestBytes.FromString($"key-{index:0000}");

    static byte[] Value(int index) => TestBytes.FromString($"value-{index:0000}");

    static (string Key, string Value) Row(int index) => ($"key-{index:0000}", $"value-{index:0000}");

    static async Task WriteAsync(IPantsDatabase database, string mode, Action<IPantsTransaction> stage)
    {
        await using var writer = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        stage(writer);
        await writer.CommitAsync(StorageModeTestHarness.GetWriteOptions(mode));
    }

    static async Task AssertPointsAsync(IPantsDatabase database, (string Key, string Value)[] expected, int count)
    {
        var values = expected.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        for (var index = 0; index < count; index++)
        {
            var value = await reader.GetAsync(Key(index));
            Assert.Equal(values.GetValueOrDefault($"key-{index:0000}"), value is null ? null : TestBytes.ToText(value.Value));
        }
    }

    static async Task<(string Key, string Value)[]> ScanAsync(IPantsDatabase database, PantsScanQuery query)
    {
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(query);
        var rows = new List<(string Key, string Value)>();
        await foreach (var entry in scan)
        {
            rows.Add((TestBytes.ToText(entry.Key), TestBytes.ToText(entry.Value)));
        }
        return rows.ToArray();
    }
}
