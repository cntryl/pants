namespace Cntryl.Pants.Tests;

public sealed class PantsTransactionParityTests
{
    [Fact]
    public async Task ShouldCommitMixedOperationsAtomicallyAndRejectFurtherUse()
    {
        using var directory = new TemporaryDirectory();
        await using (IPantsDatabase database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path)))
        {
            await SeedAsync(database, ("a", "old-a"), ("b", "old-b"), ("c", "old-c"));
            await using IPantsTransaction transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("a"u8.ToArray(), "new-a"u8.ToArray());
            transaction.Delete("b"u8.ToArray());
            transaction.DeleteRange("c"u8.ToArray(), "d"u8.ToArray());
            transaction.Insert("d"u8.ToArray(), "new-d"u8.ToArray());

            await transaction.CommitAsync(PantsWriteOptions.Sync);

            Assert.Equal("new-a", await ReadAsync(database, "a"));
            Assert.Null(await ReadAsync(database, "b"));
            Assert.Null(await ReadAsync(database, "c"));
            Assert.Equal("new-d", await ReadAsync(database, "d"));
            Assert.Throws<PantsInvalidArgumentException>(
                () => transaction.Put("after"u8.ToArray(), "commit"u8.ToArray()));
            await Assert.ThrowsAsync<PantsInvalidArgumentException>(
                () => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());
        }

        await using IPantsDatabase reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.Equal("new-a", await ReadAsync(reopened, "a"));
        Assert.Null(await ReadAsync(reopened, "b"));
        Assert.Null(await ReadAsync(reopened, "c"));
        Assert.Equal("new-d", await ReadAsync(reopened, "d"));
    }

    [Fact]
    public async Task ShouldValidateAssertionsAgainstSequenceAndSnapshotTime()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.InMemory().WithTtlClock(clock));
        await SeedAsync(database, ("aba", "original"));
        await using IPantsTransaction asserting = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        asserting.AssertValue("aba"u8.ToArray(), "original"u8.ToArray());

        await SeedAsync(database, ("aba", "changed"));
        await SeedAsync(database, ("aba", "original"));

        PantsWriteConflictException aba = await Assert.ThrowsAsync<PantsWriteConflictException>(
            () => asserting.CommitAsync(PantsWriteOptions.Buffered).AsTask());
        Assert.Equal(PantsErrorCode.WriteConflict, aba.Code);

        await using IPantsTransaction absent = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        absent.AssertValue("new"u8.ToArray(), null);
        await SeedAsync(database, ("new", "inserted"));
        await Assert.ThrowsAsync<PantsWriteConflictException>(
            () => absent.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        await using (IPantsTransaction expiring = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            expiring.Put("ttl"u8.ToArray(), "visible"u8.ToArray(), TimeSpan.FromSeconds(1));
            await expiring.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using IPantsTransaction ttlAssertion = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        ttlAssertion.AssertValue("ttl"u8.ToArray(), "visible"u8.ToArray());
        clock.UtcNow = clock.UtcNow.AddSeconds(2);
        await ttlAssertion.CommitAsync(PantsWriteOptions.Buffered);
    }

    [Fact]
    public async Task ShouldApplyInsertAndIntentOrderingAgainstFlushedState()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await SeedAsync(database, ("existing", "value"), ("point", "old"));
        await database.FlushAsync(database.DefaultColumnFamily);

        await using (IPantsTransaction duplicate = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            duplicate.Insert("existing"u8.ToArray(), "replacement"u8.ToArray());
            await Assert.ThrowsAsync<PantsInvalidArgumentException>(
                () => duplicate.CommitAsync(PantsWriteOptions.Buffered).AsTask());
        }

        await using IPantsTransaction ordered = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        ordered.Delete("point"u8.ToArray());
        ordered.Put("point"u8.ToArray(), "middle"u8.ToArray());
        ordered.DeleteRange("point"u8.ToArray(), "poinu"u8.ToArray());
        ordered.Put("point"u8.ToArray(), "final"u8.ToArray());

        Assert.Equal("final", TestBytes.ToText((await ordered.GetAsync("point"u8.ToArray()))!.Value));
        await using IPantsScan scan = await ordered.ScanAsync(new PantsScanQuery
        {
            StartInclusive = "point"u8.ToArray(),
            EndExclusive = "poinu"u8.ToArray(),
            Limit = 1
        });
        PantsEntry entry = Assert.Single(await CollectAsync(scan));
        Assert.Equal("point", TestBytes.ToText(entry.Key));
        Assert.Equal("final", TestBytes.ToText(entry.Value));
        await ordered.CommitAsync(PantsWriteOptions.Buffered);
    }

    [Fact]
    public async Task ShouldKeepTransactionAndScanSnapshotsStableAcrossFlushCompactionAndDrop()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        IPantsColumnFamily family = await database.CreateColumnFamilyAsync("snapshot");
        await SeedAsync(database, family, ("a", "one"), ("b", "two"));
        await database.FlushAsync(family);
        await using IPantsTransaction snapshot = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);
        await using IPantsScan scan = await snapshot.ScanAsync(new PantsScanQuery());

        await SeedAsync(database, family, ("a", "new"), ("c", "three"));
        await database.FlushAsync(family);
        await database.CompactAllAsync();
        await database.DropColumnFamilyAsync(family);

        Assert.Equal("one", TestBytes.ToText((await snapshot.GetAsync("a"u8.ToArray()))!.Value));
        Assert.Equal(
            ["a:one", "b:two"],
            (await CollectAsync(scan)).Select(static entry =>
                $"{TestBytes.ToText(entry.Key)}:{TestBytes.ToText(entry.Value)}"));
        Assert.Null(await database.GetColumnFamilyAsync("snapshot"));
    }

    [Fact]
    public async Task ShouldRemainConsistentUnderConcurrentStrictAndLastWriteWinsCommits()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        const int writerCount = 32;
        Task[] disjointWriters = Enumerable.Range(0, writerCount)
            .Select(index => WriteDisjointAsync(database, index))
            .ToArray();
        await Task.WhenAll(disjointWriters).WaitAsync(TimeSpan.FromSeconds(10));

        await using IPantsTransaction first = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        await using IPantsTransaction second = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        first.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
        second.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
        first.Put("contended"u8.ToArray(), "first"u8.ToArray());
        second.Put("contended"u8.ToArray(), "second"u8.ToArray());
        await first.CommitAsync(PantsWriteOptions.Buffered);
        await Assert.ThrowsAsync<PantsWriteConflictException>(
            () => second.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        for (var index = 0; index < writerCount; index++)
        {
            Assert.Equal(index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                await ReadAsync(database, $"key-{index:00}"));
        }

        Assert.Equal("first", await ReadAsync(database, "contended"));
    }

    [Fact]
    public async Task ShouldEnforceReadOnlyAndTerminalTransactionLifecycle()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction readOnly = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Throws<PantsInvalidArgumentException>(
            () => readOnly.Put("key"u8.ToArray(), "value"u8.ToArray()));
        Assert.Throws<PantsInvalidArgumentException>(
            () => readOnly.Insert("key"u8.ToArray(), "value"u8.ToArray()));
        Assert.Throws<PantsInvalidArgumentException>(() => readOnly.Delete("key"u8.ToArray()));
        Assert.Throws<PantsInvalidArgumentException>(
            () => readOnly.DeleteRange("a"u8.ToArray(), "z"u8.ToArray()));
        Assert.Throws<PantsInvalidArgumentException>(
            () => readOnly.AssertValue("key"u8.ToArray(), null));

        await readOnly.CommitAsync(PantsWriteOptions.Sync);
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(
            () => readOnly.GetAsync("key"u8.ToArray()).AsTask());
        await readOnly.RollbackAsync();
        Assert.Equal(0, (await database.GetRuntimeMetricsAsync()).ActiveSnapshots);

        await using IPantsTransaction rolledBack = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        rolledBack.Put("discarded"u8.ToArray(), "value"u8.ToArray());
        await rolledBack.RollbackAsync();
        await rolledBack.RollbackAsync();
        Assert.Null(await ReadAsync(database, "discarded"));
    }

    [Fact]
    public async Task ShouldShareTransactionMemoryBetweenAssertionsAndSpillableWrites()
    {
        using var directory = new TemporaryDirectory();
        PantsOpenOptions options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(8192))
            .WithMemtableLimits(2048)
            .WithTransactionMemoryPool(512);
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(options);
        byte[] assertedValue = Enumerable.Repeat((byte)'a', 200).ToArray();
        await using (IPantsTransaction seed = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            seed.Put("asserted"u8.ToArray(), assertedValue);
            await seed.CommitAsync(PantsWriteOptions.Sync);
        }

        await using (IPantsTransaction spilling = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            spilling.AssertValue("asserted"u8.ToArray(), assertedValue);
            spilling.Put("large"u8.ToArray(), Enumerable.Repeat((byte)'v', 1024).ToArray());
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
            await spilling.CommitAsync(PantsWriteOptions.Sync);
        }

        Assert.False(Directory.Exists(Path.Combine(directory.Path, "txn")));
        await using IPantsTransaction exhausted = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        exhausted.Put("resident"u8.ToArray(), Enumerable.Repeat((byte)'r', 400).ToArray());
        PantsResourceLimitException limit = Assert.Throws<PantsResourceLimitException>(() =>
            exhausted.AssertValue("asserted"u8.ToArray(), assertedValue));
        Assert.Equal(PantsErrorCode.ResourceLimit, limit.Code);
    }

    [Fact]
    public async Task ShouldNotAllocateSequenceForAssertionOnlyOrEmptyCommit()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        long initial = (await database.GetRuntimeMetricsAsync()).CurrentSequence;
        await using (IPantsTransaction assertionOnly = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            assertionOnly.AssertValue("missing"u8.ToArray(), null);
            await assertionOnly.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using (IPantsTransaction empty = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            await empty.CommitAsync(PantsWriteOptions.Buffered);
        }

        Assert.Equal(initial, (await database.GetRuntimeMetricsAsync()).CurrentSequence);
    }

    [Fact]
    public async Task ShouldPublishOnlyAtomicSnapshotsToConcurrentReaders()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await SeedAsync(database, ("first", "old-first"), ("second", "old-second"));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task writer = Task.Run(async () =>
        {
            await start.Task;
            await SeedAsync(database, ("first", "new-first"), ("second", "new-second"));
        });
        start.SetResult();

        for (var iteration = 0; iteration < 100; iteration++)
        {
            await using IPantsTransaction reader = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadOnly);
            string first = TestBytes.ToText((await reader.GetAsync("first"u8.ToArray()))!.Value);
            string second = TestBytes.ToText((await reader.GetAsync("second"u8.ToArray()))!.Value);
            Assert.True(
                (first == "old-first" && second == "old-second") ||
                (first == "new-first" && second == "new-second"),
                $"Observed a partial transaction snapshot: {first}, {second}.");
        }

        await writer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ShouldTrackSnapshotsAcrossCommitRollbackAndRejectedBegin()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        IPantsColumnFamily dropped = await database.CreateColumnFamilyAsync("dropped");
        await database.DropColumnFamilyAsync(dropped);
        Assert.Equal(0, (await database.GetRuntimeMetricsAsync()).ActiveSnapshots);
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(
            () => database.BeginTransactionAsync(dropped, PantsTransactionMode.ReadOnly).AsTask());
        Assert.Equal(0, (await database.GetRuntimeMetricsAsync()).ActiveSnapshots);

        await using (IPantsTransaction committed = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadOnly))
        {
            Assert.Equal(1, (await database.GetRuntimeMetricsAsync()).ActiveSnapshots);
            await committed.CommitAsync(PantsWriteOptions.Buffered);
        }

        Assert.Equal(0, (await database.GetRuntimeMetricsAsync()).ActiveSnapshots);
        await using (IPantsTransaction rolledBack = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadOnly))
        {
            Assert.Equal(1, (await database.GetRuntimeMetricsAsync()).ActiveSnapshots);
            await rolledBack.RollbackAsync();
        }

        Assert.Equal(0, (await database.GetRuntimeMetricsAsync()).ActiveSnapshots);
    }

    [Fact]
    public async Task ShouldBoundRuntimeStateAcrossTwoThousandCommits()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        for (var index = 0; index < 2_000; index++)
        {
            await using IPantsTransaction transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(
                TestBytes.FromString($"key-{index:0000}"),
                "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        PantsRuntimeMetrics metrics = await database.GetRuntimeMetricsAsync();
        Assert.Equal(0, metrics.ActiveSnapshots);
        Assert.Equal(2_000, metrics.CurrentSequence);
        Assert.Equal("value", await ReadAsync(database, "key-1999"));
    }

    private static async Task WriteDisjointAsync(IPantsDatabase database, int index)
    {
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
        transaction.Put(
            TestBytes.FromString($"key-{index:00}"),
            TestBytes.FromString(index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
    }

    private static Task SeedAsync(
        IPantsDatabase database,
        params (string Key, string Value)[] entries) =>
        SeedAsync(database, database.DefaultColumnFamily, entries);

    private static async Task SeedAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        params (string Key, string Value)[] entries)
    {
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            columnFamily,
            PantsTransactionMode.ReadWrite);
        foreach ((string key, string value) in entries)
        {
            transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        }

        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    private static async Task<string?> ReadAsync(IPantsDatabase database, string key)
    {
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        ReadOnlyMemory<byte>? value = await transaction.GetAsync(TestBytes.FromString(key));
        return value is null ? null : TestBytes.ToText(value.Value);
    }

    private static async Task<IReadOnlyList<PantsEntry>> CollectAsync(IPantsScan scan)
    {
        var entries = new List<PantsEntry>();
        await foreach (PantsEntry entry in scan)
        {
            entries.Add(entry);
        }

        return entries;
    }
}
