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
            directory.Path);
        var expected = new Dictionary<string, byte[]>();
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 8; index++)
            {
                var key = $"key-{index:0000}";
                var value = Enumerable.Repeat(checked((byte)('a' + index)), 900).ToArray();
                expected[key] = value;
                transaction.Put(TestBytes.FromString(key), value);
            }

            Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
            await transaction.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));
        }

        foreach (var pair in expected)
        {
            Assert.Equal(
                pair.Value,
                (await TransactionSpillTestHarness.GetAsync(
                    database,
                    TestBytes.FromString(pair.Key)))!.Value.ToArray());
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
            directory.Path);
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 12; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"big-key-{index:0000}"),
                    Enumerable.Repeat((byte)'v', 900).ToArray());
            }

            Assert.True(TransactionSpillTestHarness.FindArtifacts(directory.Path)
                .Count(static path => path.EndsWith(".run", StringComparison.Ordinal)) >= 2);
            await transaction.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));
        }

        Assert.NotNull(await TransactionSpillTestHarness.GetAsync(database, "big-key-0000"u8.ToArray()));
        Assert.NotNull(await TransactionSpillTestHarness.GetAsync(database, "big-key-0011"u8.ToArray()));
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
            directory.Path);
        var expected = new Dictionary<string, byte[]>();
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 9; index++)
            {
                var key = $"integrity-test-{index:0000}";
                var prefix = TestBytes.FromString($"pattern-{index % 3}-");
                var value = prefix
                    .Concat(Enumerable.Repeat(checked((byte)('a' + index)), 900 - prefix.Length))
                    .ToArray();
                expected[key] = value;
                transaction.Put(TestBytes.FromString(key), value);
            }

            await transaction.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));
        }

        foreach (var pair in expected)
        {
            Assert.Equal(
                pair.Value,
                (await TransactionSpillTestHarness.GetAsync(
                    database,
                    TestBytes.FromString(pair.Key)))!.Value.ToArray());
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
            directory.Path);
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 11; index >= 0; index--)
            {
                transaction.Put(
                    TestBytes.FromString($"order-test-{index:0000}"),
                    Enumerable.Repeat((byte)'v', 900).ToArray());
            }

            await transaction.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));
        }

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var actual = new List<string>();
        await foreach (var entry in scan)
        {
            actual.Add(TestBytes.ToText(entry.Key));
        }

        var expected = Enumerable.Range(0, 12)
            .Select(static index => $"order-test-{index:0000}")
            .ToArray();
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(SpillStorageMode.Local)]
    [InlineData(SpillStorageMode.Cloud)]
    public async Task ShouldRollbackSpilledTransactionGivenDropWithoutCommit(SpillStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillTestHarness.OpenAsync(
            mode,
            directory.Path);
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 4; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"rollback-test-{index:0000}"),
                    Enumerable.Repeat((byte)'v', 900).ToArray());
            }

            Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
        }

        Assert.Null(await TransactionSpillTestHarness.GetAsync(
            database,
            "rollback-test-0000"u8.ToArray()));
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
            directory.Path);
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 4; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"spill-cleanup-{index:0000}"),
                    Enumerable.Repeat((byte)'v', 900).ToArray());
            }

            Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
            await transaction.RollbackAsync();
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
    }

    [Theory]
    [InlineData(SpillStorageMode.Local)]
    [InlineData(SpillStorageMode.Cloud)]
    public async Task ShouldRollbackUncommittedSpillGivenRestartBeforeCommit(SpillStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await TransactionSpillTestHarness.OpenAsync(
                         mode,
                         directory.Path))
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            for (var index = 0; index < 4; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"uncommitted-spill-{index:0000}"),
                    Enumerable.Repeat((byte)'v', 900).ToArray());
            }

            Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
        }

        await using var reopened = await TransactionSpillTestHarness.OpenAsync(
            mode,
            directory.Path);

        Assert.Empty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
        Assert.Null(await TransactionSpillTestHarness.GetAsync(
            reopened,
            "uncommitted-spill-0000"u8.ToArray()));
    }

    [Theory]
    [InlineData(SpillStorageMode.Local)]
    [InlineData(SpillStorageMode.Cloud)]
    public async Task ShouldRecoverCommittedSpillGivenRestartAfterCommit(SpillStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await TransactionSpillTestHarness.OpenAsync(
                         mode,
                         directory.Path))
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            for (var index = 0; index < 4; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"committed-spill-{index:0000}"),
                    Enumerable.Repeat((byte)'v', 900).ToArray());
            }

            Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
            await transaction.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));
        }

        await using var reopened = await TransactionSpillTestHarness.OpenAsync(
            mode,
            directory.Path);

        Assert.Empty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
        Assert.Equal(
            Enumerable.Repeat((byte)'v', 900),
            (await TransactionSpillTestHarness.GetAsync(
                reopened,
                "committed-spill-0000"u8.ToArray()))!.Value.ToArray());
    }

    [Theory]
    [InlineData(SpillStorageMode.Local)]
    [InlineData(SpillStorageMode.Cloud)]
    public async Task ShouldNotStarveForegroundWritesGivenBackgroundSpillActivity(
        SpillStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillTestHarness.OpenAsync(
            mode,
            directory.Path);
        await using var spilling = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        for (var index = 0; index < 8; index++)
        {
            spilling.Put(
                TestBytes.FromString($"large-{index:0000}"),
                Enumerable.Repeat((byte)'v', 800).ToArray());
        }

        Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
        await using (var foreground = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            foreground.Put("foreground"u8.ToArray(), "works"u8.ToArray());
            await foreground
                .CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
        }

        await spilling.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));

        Assert.Equal(
            "works",
            TestBytes.ToText((await TransactionSpillTestHarness.GetAsync(
                database,
                "foreground"u8.ToArray()))!.Value));
    }

    [Theory]
    [InlineData(SpillStorageMode.Local)]
    [InlineData(SpillStorageMode.Cloud)]
    public async Task ShouldHandleConcurrentLargeTransactionsGivenMemoryPressure(
        SpillStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            TransactionSpillTestHarness.CreateOptions(mode, directory.Path)
                .WithMemoryBudget(PantsMemoryBudget.FromBytes(256 * 1_024))
                .WithMemtableLimits(64 * 1_024));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var stagedCount = 0;

        async Task WriteAsync(string prefix, byte value)
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            if (Interlocked.Increment(ref readyCount) == 2)
            {
                ready.TrySetResult();
            }

            await start.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < 8; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"{prefix}-key-{index:0000}"),
                    Enumerable.Repeat(value, 800).ToArray());
            }

            if (Interlocked.Increment(ref stagedCount) == 2)
            {
                staged.TrySetResult();
            }

            await staged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await transaction.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));
        }

        var first = WriteAsync("t1", (byte)'1');
        var second = WriteAsync("t2", (byte)'2');
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        start.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(await TransactionSpillTestHarness.GetAsync(database, "t1-key-0000"u8.ToArray()));
        Assert.NotNull(await TransactionSpillTestHarness.GetAsync(database, "t2-key-0000"u8.ToArray()));
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
            512);
        var expected = Enumerable.Repeat((byte)'v', 400).ToArray();
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 6; index++)
            {
                transaction.Put(TestBytes.FromString($"tiny-{index:00}"), expected);
            }

            Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
            await transaction.CommitAsync(TransactionSpillTestHarness.GetWriteOptions(mode));
        }

        Assert.Equal(
            expected,
            (await TransactionSpillTestHarness.GetAsync(
                database,
                "tiny-00"u8.ToArray()))!.Value.ToArray());
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
            512);
        var expected = new Dictionary<string, byte[]>
        {
            ["mixed-0000"] = "tiny"u8.ToArray(),
            ["mixed-0001"] = Enumerable.Repeat((byte)'x', 256).ToArray(),
            ["mixed-0002"] = Enumerable.Repeat((byte)'y', 512).ToArray(),
            ["mixed-0003"] = "tiny"u8.ToArray(),
            ["mixed-0004"] = Enumerable.Repeat((byte)'x', 256).ToArray(),
            ["mixed-0005"] = Enumerable.Repeat((byte)'y', 512).ToArray()
        };
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

        foreach (var pair in expected)
        {
            Assert.Equal(
                pair.Value,
                (await TransactionSpillTestHarness.GetAsync(
                    database,
                    TestBytes.FromString(pair.Key)))!.Value.ToArray());
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
