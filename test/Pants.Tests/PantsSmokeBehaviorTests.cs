namespace Pants.Tests;

public sealed class PantsSmokeBehaviorTests
{
    [Fact]
    public async Task ShouldAllowReadOnlySnapshotGivenCommittedValue()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("key"u8.ToArray(), "value"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using var snapshot = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal("value", TestBytes.ToText((await snapshot.GetAsync("key"u8.ToArray()))!.Value));
    }

    [Fact]
    public async Task ShouldRespectVisibilityRulesGivenDeletedKeyWhenScanning()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("a"u8.ToArray(), "1"u8.ToArray());
            writer.Put("b"u8.ToArray(), "2"u8.ToArray());
            writer.Put("c"u8.ToArray(), "3"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using (var deleting = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            deleting.Delete("b"u8.ToArray());
            await deleting.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery
        {
            StartInclusive = "a"u8.ToArray(),
            EndExclusive = "d"u8.ToArray()
        });
        var keys = new List<string>();
        await foreach (var entry in scan)
        {
            keys.Add(TestBytes.ToText(entry.Key));
        }

        Assert.Equal(["a", "c"], keys);
    }
}
