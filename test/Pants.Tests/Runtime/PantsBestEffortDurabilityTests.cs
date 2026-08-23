namespace Cntryl.Pants.Tests;

public sealed class PantsBestEffortDurabilityTests
{
    [Fact]
    public async Task ShouldSkipWalAndExposeMemtableGivenBestEffortCommit()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));

        await CommitAsync(database, PantsWriteOptions.BestEffort, ("key-1", "value-1"), ("key-2", "value-2"));

        Assert.Equal(0, new FileInfo(Path.Combine(directory.Path, "wal", "wal.log")).Length);
        Assert.Equal("value-1", await ReadAsync(database, "key-1"));
        Assert.Equal("value-2", await ReadAsync(database, "key-2"));
    }

    [Fact]
    public async Task ShouldPersistBestEffortDataGivenExplicitFlushBeforeReopen()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)))
        {
            var entries = Enumerable.Range(0, 100)
                .Select(static index => ($"key-{index}", $"value-{index}"))
                .ToArray();
            await CommitAsync(database, PantsWriteOptions.BestEffort, entries);
            await database.FlushAsync(database.DefaultColumnFamily);
            await CommitAsync(database, PantsWriteOptions.Buffered, ("durable-marker", "marker"));
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));

        Assert.Equal("value-0", await ReadAsync(reopened, "key-0"));
        Assert.Equal("marker", await ReadAsync(reopened, "durable-marker"));
    }

    [Fact]
    public async Task ShouldLoseBestEffortDataGivenShutdownWithoutFlush()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)))
        {
            await CommitAsync(database, PantsWriteOptions.BestEffort, ("ephemeral", "value"));
            Assert.Equal("value", await ReadAsync(database, "ephemeral"));
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));

        Assert.Null(await ReadAsync(reopened, "ephemeral"));
    }

    [Fact]
    public async Task ShouldHandleFiftyThousandOperationsGivenBestEffortBatch()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        for (var index = 0; index < 50_000; index++)
        {
            transaction.Put(
                TestBytes.FromString($"key-{index:00000000}"),
                TestBytes.FromString($"value-{index:00000000}"));
        }

        await transaction.CommitAsync(PantsWriteOptions.BestEffort);

        Assert.Equal("value-00000000", await ReadAsync(database, "key-00000000"));
        Assert.Equal("value-00025000", await ReadAsync(database, "key-00025000"));
        Assert.Equal("value-00049999", await ReadAsync(database, "key-00049999"));
    }

    static async Task CommitAsync(
        IPantsDatabase database,
        PantsWriteOptions writeOptions,
        params (string Key, string Value)[] entries)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        foreach (var (key, value) in entries)
        {
            transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        }

        await transaction.CommitAsync(writeOptions);
    }

    static async Task<string?> ReadAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(TestBytes.FromString(key));
        return value is null ? null : TestBytes.ToText(value.Value);
    }
}
