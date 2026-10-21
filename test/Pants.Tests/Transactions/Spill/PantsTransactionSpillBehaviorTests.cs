using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions.Spill;

public sealed class PantsTransactionSpillBehaviorTests
{
    [Theory]
    [InlineData(SpillStorageMode.Local)]
    [InlineData(SpillStorageMode.Cloud)]
    public async Task ShouldCommitLargeTransactionGivenManyWritesExceedingMemoryLimit(
        SpillStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillTestHarness.OpenAsync(
            mode,
            directory.Path,
            transactionMemoryPoolBytes: 4 * 1_024);
        var expected = new Dictionary<string, byte[]>();
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 100; index++)
            {
                var key = $"key{index:0000}";
                var value = TestBytes.FromString($"value_{index:0000}");
                expected[key] = value;
                transaction.Put(TestBytes.FromString(key), value);
            }

            Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
            await transaction.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));
        }

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        foreach (var pair in expected)
        {
            var value = await reader.GetAsync(TestBytes.FromString(pair.Key));
            Assert.NotNull(value);
            Assert.Equal(
                pair.Value,
                value.Value.ToArray());
        }
    }

    [Theory]
    [InlineData(SpillStorageMode.Local)]
    [InlineData(SpillStorageMode.Cloud)]
    public async Task ShouldHandleVeryLargeTransactionGivenMultipleSpillsWhenPersisted(
        SpillStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillTestHarness.OpenAsync(
            mode,
            directory.Path,
            transactionMemoryPoolBytes: 8 * 1_024);
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 500; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"big_key{index:0000}"),
                    TestBytes.FromString($"big_value_{index:0000}"));
            }

            Assert.True(TransactionSpillTestHarness.FindArtifacts(directory.Path)
                .Count(static path => path.EndsWith(".run", StringComparison.Ordinal)) >= 2);
            await transaction.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));
        }

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 500; index++)
        {
            var key = $"big_key{index:0000}";
            Assert.Equal(
                $"big_value_{index:0000}",
                TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                    await reader.GetAsync(TestBytes.FromString(key)))));
        }
    }

    [Theory]
    [InlineData(SpillStorageMode.Local)]
    [InlineData(SpillStorageMode.Cloud)]
    public async Task ShouldPreserveDataIntegrityGivenLargeTransactionWithSpecificValues(
        SpillStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillTestHarness.OpenAsync(
            mode,
            directory.Path,
            transactionMemoryPoolBytes: 8 * 1_024);
        var expected = new Dictionary<string, byte[]>();
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 200; index++)
            {
                var key = $"integrity_test_{index:0000}";
                var prefix = TestBytes.FromString($"pattern_{index % 10}_");
                var value = prefix
                    .Concat(Enumerable.Repeat((byte)'x', 50))
                    .ToArray();
                expected[key] = value;
                transaction.Put(TestBytes.FromString(key), value);
            }

            Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
            await transaction.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));
        }

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        foreach (var pair in expected)
        {
            var value = await reader.GetAsync(TestBytes.FromString(pair.Key));
            Assert.NotNull(value);
            Assert.Equal(
                pair.Value,
                value.Value.ToArray());
        }
    }

    [Theory]
    [InlineData(SpillStorageMode.Local)]
    [InlineData(SpillStorageMode.Cloud)]
    public async Task ShouldPreserveKeyOrderGivenLargeTransactionWhenIterating(
        SpillStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillTestHarness.OpenAsync(
            mode,
            directory.Path,
            transactionMemoryPoolBytes: 8 * 1_024);
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 200; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"order_test_{index:0000}"),
                    "v"u8.ToArray());
            }

            Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
            await transaction.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));
        }

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var actual = new List<string>();
        var entries = new List<PantsEntry>();
        await foreach (var entry in scan)
        {
            actual.Add(TestBytes.ToText(entry.Key));
            entries.Add(entry);
        }

        var expected = Enumerable.Range(0, 200)
            .Select(static index => $"order_test_{index:0000}")
            .ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal(200, actual.Count);
        Assert.All(entries, static entry => Assert.Equal("v", TestBytes.ToText(entry.Value)));
    }

    [Theory]
    [InlineData(SpillStorageMode.Local)]
    [InlineData(SpillStorageMode.Cloud)]
    public async Task ShouldRollbackSpilledTransactionGivenDropWithoutCommit(SpillStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillTestHarness.OpenAsync(
            mode,
            directory.Path,
            transactionMemoryPoolBytes: 8 * 1_024);
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 200; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"rollback_test_{index:0000}"),
                    "value"u8.ToArray());
            }

            Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
        }

        Assert.Empty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 200; index++)
        {
            Assert.Null(await reader.GetAsync(TestBytes.FromString($"rollback_test_{index:0000}")));
        }
    }

    [Theory]
    [InlineData(SpillStorageMode.Local)]
    [InlineData(SpillStorageMode.Cloud)]
    public async Task ShouldCleanupSpillFilesGivenTransactionRollbackWhenFinalizing(
        SpillStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillTestHarness.OpenAsync(
            mode,
            directory.Path,
            transactionMemoryPoolBytes: 8 * 1_024);
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 300; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"spill_cleanup_{index:0000}"),
                    "value"u8.ToArray());
            }

            Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
        }

        Assert.Empty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("test"u8.ToArray(), "value"u8.ToArray());
            await writer.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));
        }

        Assert.Equal(
            "value",
            TestBytes.ToText((await TransactionSpillTestHarness.GetAsync(
                database,
                "test"u8.ToArray()))!.Value));
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 300; index++)
        {
            Assert.Null(await reader.GetAsync(TestBytes.FromString($"spill_cleanup_{index:0000}")));
        }
    }

    [Theory]
    [InlineData(SpillStorageMode.Local)]
    [InlineData(SpillStorageMode.Cloud)]
    public async Task ShouldRollbackUncommittedSpillGivenRestartBeforeCommit(SpillStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await TransactionSpillTestHarness.OpenAsync(
                         mode,
                         directory.Path,
                         transactionMemoryPoolBytes: 8 * 1_024))
        {
            await using (var transaction = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                for (var index = 0; index < 300; index++)
                {
                    transaction.Put(
                        TestBytes.FromString($"uncom_spill_{index:0000}"),
                        "value"u8.ToArray());
                }

                Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
            }

            Assert.Empty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
        }

        await using var reopened = await TransactionSpillTestHarness.OpenAsync(
            mode,
            directory.Path,
            transactionMemoryPoolBytes: 8 * 1_024);

        Assert.Empty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 300; index++)
        {
            Assert.Null(await reader.GetAsync(TestBytes.FromString($"uncom_spill_{index:0000}")));
        }
    }

    [Theory]
    [InlineData(SpillStorageMode.Local)]
    [InlineData(SpillStorageMode.Cloud)]
    public async Task ShouldRecoverCommittedSpillGivenRestartAfterCommit(SpillStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await TransactionSpillTestHarness.OpenAsync(
                         mode,
                         directory.Path,
                         transactionMemoryPoolBytes: 8 * 1_024))
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            for (var index = 0; index < 300; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"com_spill_{index:0000}"),
                    "value"u8.ToArray());
            }

            Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
            await transaction.CommitAsync(TransactionSpillTestHarness.GetSyncWriteOptions(mode));
        }

        await using var reopened = await TransactionSpillTestHarness.OpenAsync(
            mode,
            directory.Path,
            transactionMemoryPoolBytes: 8 * 1_024);

        Assert.Empty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 300; index++)
        {
            Assert.Equal(
                "value",
                TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                    await reader.GetAsync(TestBytes.FromString($"com_spill_{index:0000}")))));
        }
    }

    [Theory]
    [InlineData(SpillStorageMode.Local)]
    [InlineData(SpillStorageMode.Cloud)]
    public async Task ShouldNotStarveForegroundWritesGivenBackgroundSpillActivity(
        SpillStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        var options = TransactionSpillTestHarness.CreateOptions(
                mode,
                directory.Path,
                transactionMemoryPoolBytes: 64 * 1_024)
            .WithBackgroundCompaction(true);
        await using var database = await PantsDatabase.OpenAsync(options);
        var stagedFirstSpill = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSpill = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopBackground = 0;
        var backgroundCommits = 0;

        async Task RunBackgroundSpillsAsync()
        {
            var round = 0;
            var firstSpill = true;
            while (Volatile.Read(ref stopBackground) == 0)
            {
                var commitSucceeded = false;
                await using (var transaction = await database.Transactions.BeginAsync(
                                 database.ColumnFamilies.DefaultFamily,
                                 PantsTransactionMode.ReadWrite))
                {
                    for (var index = 0; index < 300; index++)
                    {
                        transaction.Put(
                            TestBytes.FromString($"bg_{round}_{index:0000}"),
                            Enumerable.Repeat((byte)'b', 512).ToArray());
                    }

                    Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
                    if (firstSpill)
                    {
                        firstSpill = false;
                        stagedFirstSpill.TrySetResult();
                        await releaseFirstSpill.Task.WaitAsync(TestTimeouts.Expected);
                    }

                    try
                    {
                        await transaction.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));
                        commitSucceeded = true;
                    }
                    catch (PantsWriteStallException)
                    {
                        Assert.True(await database.Maintenance.WaitForWriteStallClearAsync(
                            database.ColumnFamilies.DefaultFamily,
                            TestTimeouts.Expected));
                    }
                }

                if (commitSucceeded)
                {
                    round++;
                    Interlocked.Increment(ref backgroundCommits);
                }

                await Task.Yield();
            }
        }

        var background = Task.Run(RunBackgroundSpillsAsync);
        var maximumElapsed = TimeSpan.FromSeconds(30);
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await stagedFirstSpill.Task.WaitAsync(TestTimeouts.Expected);
            for (var index = 0; index < 50; index++)
            {
                var key = $"foreground_{index:0000}";
                while (true)
                {
                    await using var foreground = await database.Transactions.BeginAsync(
                        database.ColumnFamilies.DefaultFamily,
                        PantsTransactionMode.ReadWrite);
                    foreground.Put(TestBytes.FromString(key), "works"u8.ToArray());
                    try
                    {
                        await foreground.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));
                        break;
                    }
                    catch (PantsWriteStallException) when (started.Elapsed < maximumElapsed)
                    {
                        Assert.True(await database.Maintenance.WaitForWriteStallClearAsync(
                            database.ColumnFamilies.DefaultFamily,
                            TestTimeouts.Expected));
                    }
                }

                Assert.True(started.Elapsed < maximumElapsed, $"Foreground write {index} starved.");
                if (index == 0)
                {
                    releaseFirstSpill.TrySetResult();
                }
            }
        }
        finally
        {
            releaseFirstSpill.TrySetResult();
            Interlocked.Exchange(ref stopBackground, 1);
            await background.WaitAsync(TestTimeouts.Expected);
        }

        var elapsed = started.Elapsed;
        Assert.True(elapsed < maximumElapsed, $"Foreground writes starved after {elapsed}.");
        Assert.True(Volatile.Read(ref backgroundCommits) > 0);

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 50; index++)
        {
            Assert.Equal("works", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync(TestBytes.FromString($"foreground_{index:0000}")))));
        }

        for (var round = 0; round < Volatile.Read(ref backgroundCommits); round++)
        {
            for (var index = 0; index < 300; index++)
            {
                Assert.Equal(
                    Enumerable.Repeat((byte)'b', 512),
                    Assert.IsType<ReadOnlyMemory<byte>>(
                        await reader.GetAsync(TestBytes.FromString($"bg_{round}_{index:0000}"))).ToArray());
            }
        }
    }

    [Theory]
    [InlineData(SpillStorageMode.Local)]
    [InlineData(SpillStorageMode.Cloud)]
    public async Task ShouldHandleConcurrentLargeTransactionsGivenMemoryPressure(
        SpillStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillTestHarness.OpenAsync(
            mode,
            directory.Path,
            transactionMemoryPoolBytes: 8 * 1_024);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var stagedCount = 0;

        async Task WriteAsync(string prefix)
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            if (Interlocked.Increment(ref readyCount) == 2)
            {
                ready.TrySetResult();
            }

            await start.Task.WaitAsync(TestTimeouts.Expected);
            for (var index = 0; index < 200; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"{prefix}_key_{index:0000}"),
                    TestBytes.FromString($"{prefix}_value"));
            }

            Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));

            if (Interlocked.Increment(ref stagedCount) == 2)
            {
                staged.TrySetResult();
            }

            await staged.Task.WaitAsync(TestTimeouts.Expected);
            await transaction.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));
        }

        var first = WriteAsync("t1");
        var second = WriteAsync("t2");
        await ready.Task.WaitAsync(TestTimeouts.Expected);
        start.TrySetResult();
        await staged.Task.WaitAsync(TestTimeouts.Expected);
        Assert.True(TransactionSpillTestHarness.FindArtifacts(directory.Path)
            .Count(static path => path.EndsWith(".run", StringComparison.Ordinal)) >= 2);
        await Task.WhenAll(first, second).WaitAsync(TestTimeouts.Expected);

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 200; index++)
        {
            Assert.Equal("t1_value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync(TestBytes.FromString($"t1_key_{index:0000}")))));
            Assert.Equal("t2_value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync(TestBytes.FromString($"t2_key_{index:0000}")))));
        }
    }

    [Theory]
    [InlineData(SpillStorageMode.Local)]
    [InlineData(SpillStorageMode.Cloud)]
    public async Task ShouldHandleTransactionWithTinyMemoryLimitGivenForcedSpill(
        SpillStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillTestHarness.OpenAsync(
            mode,
            directory.Path,
            transactionMemoryPoolBytes: 1_024);
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 20; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"tiny_{index:00}"),
                    TestBytes.FromString($"value{index:00}"));
            }

            Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
            await transaction.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));
        }

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 20; index++)
        {
            Assert.Equal(
                $"value{index:00}",
                TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                    await reader.GetAsync(TestBytes.FromString($"tiny_{index:00}")))));
        }
    }

    [Theory]
    [InlineData(SpillStorageMode.Local)]
    [InlineData(SpillStorageMode.Cloud)]
    public async Task ShouldHandleMixedValueSizesInSpilledTransactionWhenCommitted(
        SpillStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillTestHarness.OpenAsync(
            mode,
            directory.Path,
            transactionMemoryPoolBytes: 8 * 1_024);
        var expected = new Dictionary<string, byte[]>();
        for (var index = 0; index < 100; index++)
        {
            var value = (index % 3) switch
            {
                0 => "tiny"u8.ToArray(),
                1 => Enumerable.Repeat((byte)'x', 256).ToArray(),
                _ => Enumerable.Repeat((byte)'y', 512).ToArray()
            };
            expected[$"mixed_{index:0000}"] = value;
        }

        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            foreach (var pair in expected)
            {
                transaction.Put(TestBytes.FromString(pair.Key), pair.Value);
            }

            Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
            await transaction.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));
        }

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        foreach (var pair in expected)
        {
            var value = await reader.GetAsync(TestBytes.FromString(pair.Key));
            Assert.NotNull(value);
            Assert.Equal(
                pair.Value,
                value.Value.ToArray());
        }
    }

    [Fact]
    public async Task ShouldNotCreateDiskArtifactsGivenLargeTransactionWhenMemoryMode()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 500; index++)
            {
                transaction.Put(TestBytes.FromString($"mem-only-{index:0000}"), "value"u8.ToArray());
            }

            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        Assert.Equal(
            "value",
            TestBytes.ToText((await TransactionSpillTestHarness.GetAsync(
                database,
                "mem-only-0000"u8.ToArray()))!.Value));
        Assert.Empty((await database.Diagnostics.GetStorageLayoutAsync()).Levels);
    }
}
