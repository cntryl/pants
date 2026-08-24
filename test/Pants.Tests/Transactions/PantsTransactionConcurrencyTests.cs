namespace Cntryl.Pants.Tests.Transactions;

public sealed class PantsTransactionConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldApplyLaterOverlappingWriteGivenLastWriteWins(bool reverseCommitOrder)
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var first = await BeginWriteAsync(database);
        await using var second = await BeginWriteAsync(database);
        first.Put("key"u8.ToArray(), "first"u8.ToArray());
        second.Put("key"u8.ToArray(), "second"u8.ToArray());

        if (reverseCommitOrder)
        {
            await second.CommitAsync(PantsWriteOptions.BestEffort);
            await first.CommitAsync(PantsWriteOptions.BestEffort);
        }
        else
        {
            await first.CommitAsync(PantsWriteOptions.BestEffort);
            await second.CommitAsync(PantsWriteOptions.BestEffort);
        }

        await using var read = await BeginReadAsync(database);
        Assert.Equal(
            reverseCommitOrder ? "first" : "second",
            TestBytes.ToText((await read.GetAsync("key"u8.ToArray()))!.Value));
        await using var scan = await read.ScanAsync(new PantsScanQuery());
        Assert.Single(await ReadAllAsync(scan));
    }

    [Fact]
    public async Task ShouldRejectConcreteAssertionGivenConcurrentValueChange()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await PutAsync(database, "key", "v1");
        await using var stale = await BeginWriteAsync(database);
        stale.AssertValue("key"u8.ToArray(), "v1"u8.ToArray());
        stale.Put("unrelated"u8.ToArray(), "value"u8.ToArray());
        await PutAsync(database, "key", "v2");

        var conflict = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
            stale.CommitAsync(PantsWriteOptions.BestEffort).AsTask());

        Assert.False(conflict.IsRangeConflict);
        await using var read = await BeginReadAsync(database);
        Assert.Equal("v2", TestBytes.ToText((await read.GetAsync("key"u8.ToArray()))!.Value));
        Assert.Null(await read.GetAsync("unrelated"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldKeepPointAndScanReadsPinnedAcrossConcurrentCommit()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await PutAsync(database, "key", "v0");
        await PutAsync(database, "other", "present");
        await using var snapshot = await BeginReadAsync(database);
        Assert.Equal("v0", TestBytes.ToText((await snapshot.GetAsync("key"u8.ToArray()))!.Value));
        await using var concurrent = await BeginWriteAsync(database);
        concurrent.Put("key"u8.ToArray(), "v1"u8.ToArray());
        concurrent.Delete("other"u8.ToArray());
        await concurrent.CommitAsync(PantsWriteOptions.BestEffort);

        Assert.Equal("v0", TestBytes.ToText((await snapshot.GetAsync("key"u8.ToArray()))!.Value));
        await using var scan = await snapshot.ScanAsync(new PantsScanQuery());
        Assert.Equal(2, (await ReadAllAsync(scan)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectSecondConcurrentInsertRegardlessOfConflictPolicy(bool abortOnConflict)
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var first = await BeginWriteAsync(database);
        await using var second = await BeginWriteAsync(database);
        if (abortOnConflict)
        {
            first.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
            second.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
        }

        first.Insert("key"u8.ToArray(), "first"u8.ToArray());
        second.Insert("key"u8.ToArray(), "second"u8.ToArray());
        await first.CommitAsync(PantsWriteOptions.BestEffort);

        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            second.CommitAsync(PantsWriteOptions.BestEffort).AsTask());
        await using var read = await BeginReadAsync(database);
        Assert.Equal("first", TestBytes.ToText((await read.GetAsync("key"u8.ToArray()))!.Value));
    }

    [Fact]
    public async Task ShouldNotImplicitlyProtectPointReadGivenAbortOnWriteConflict()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await PutAsync(database, "observed", "v0");
        await using var stale = await BeginWriteAsync(database);
        stale.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
        Assert.Equal("v0", TestBytes.ToText((await stale.GetAsync("observed"u8.ToArray()))!.Value));
        stale.Put("unrelated"u8.ToArray(), "committed"u8.ToArray());
        await PutAsync(database, "observed", "v1");

        await stale.CommitAsync(PantsWriteOptions.BestEffort);

        await using var read = await BeginReadAsync(database);
        Assert.Equal("v1", TestBytes.ToText((await read.GetAsync("observed"u8.ToArray()))!.Value));
        Assert.Equal("committed", TestBytes.ToText((await read.GetAsync("unrelated"u8.ToArray()))!.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldNotImplicitlyProtectScannedRangeGivenConcurrentMutation(bool insertPhantom)
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await PutAsync(database, "a2", "v0");
        await using var stale = await BeginWriteAsync(database);
        stale.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
        await using (var scan = await stale.ScanAsync(new PantsScanQuery
        {
            Prefix = "a"u8.ToArray()
        }))
        {
            Assert.Single(await ReadAllAsync(scan));
        }

        stale.Put("unrelated"u8.ToArray(), "committed"u8.ToArray());
        await PutAsync(database, insertPhantom ? "a2b" : "a2", "concurrent");

        await stale.CommitAsync(PantsWriteOptions.BestEffort);
        await using var read = await BeginReadAsync(database);
        Assert.Equal("committed", TestBytes.ToText((await read.GetAsync("unrelated"u8.ToArray()))!.Value));
    }

    static ValueTask<IPantsTransaction> BeginWriteAsync(IPantsDatabase database) =>
        database.BeginTransactionAsync(database.DefaultColumnFamily, PantsTransactionMode.ReadWrite);

    static ValueTask<IPantsTransaction> BeginReadAsync(IPantsDatabase database) =>
        database.BeginTransactionAsync(database.DefaultColumnFamily, PantsTransactionMode.ReadOnly);

    static async Task PutAsync(IPantsDatabase database, string key, string value)
    {
        await using var transaction = await BeginWriteAsync(database);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.BestEffort);
    }

    static async Task<List<PantsEntry>> ReadAllAsync(IPantsScan scan)
    {
        var entries = new List<PantsEntry>();
        await foreach (var entry in scan)
        {
            entries.Add(entry);
        }

        return entries;
    }
}
