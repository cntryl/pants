using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsMemtableConcurrencyTests
{
    [Fact]
    public async Task ShouldKeepPinnedScanStableWhileConcurrentCommitsPublishNewSnapshots()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await WriteGenerationAsync(database, "old");
        await using var snapshot = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await snapshot.ScanAsync(new PantsScanQuery());

        var writer = WriteGenerationAsync(database, "new");
        var observed = new List<string>();
        await foreach (var entry in scan)
        {
            observed.Add($"{TestBytes.ToText(entry.Key)}:{TestBytes.ToText(entry.Value)}");
            await Task.Yield();
        }

        await writer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            Enumerable.Range(0, 100).Select(static index => $"key-{index:000}:old"),
            observed);
    }

    [Fact]
    public async Task ShouldApplySequenceVisibilityAtTheTransactionSnapshotBoundary()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await WriteValueAsync(database, "version", "one");
        await using var pinned = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        await WriteValueAsync(database, "version", "two");

        Assert.Equal("one", TestBytes.ToText((await pinned.GetAsync("version"u8.ToArray()))!.Value));
        await using var current = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("two", TestBytes.ToText((await current.GetAsync("version"u8.ToArray()))!.Value));
    }

    static async Task WriteGenerationAsync(IPantsDatabase database, string value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        for (var index = 0; index < 100; index++)
        {
            transaction.Put(TestBytes.FromString($"key-{index:000}"), TestBytes.FromString(value));
        }

        await transaction.CommitAsync(PantsWriteOptions.Buffered);
    }

    static async Task WriteValueAsync(IPantsDatabase database, string key, string value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
    }
}
