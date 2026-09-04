using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants;

public sealed class PantsExternalAdopterSmokeTests
{
    [Fact]
    public async Task ShouldFilterPointDeleteTombstonesGivenCrossSstScan()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        for (var batch = 0; batch < 2; batch++)
        {
            await using var writer = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            for (var index = 0; index < 20; index++)
            {
                var number = batch * 20 + index;
                writer.Put(
                    TestBytes.FromString($"key-{number:000}"),
                    TestBytes.FromString($"value-{number:000}"));
            }

            await writer.CommitAsync(PantsWriteOptions.Buffered);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        await using (var deleting = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            deleting.Delete("key-010"u8.ToArray());
            deleting.Delete("key-011"u8.ToArray());
            deleting.Delete("key-024"u8.ToArray());
            await deleting.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var keys = new List<string>();
        await foreach (var entry in scan)
        {
            keys.Add(TestBytes.ToText(entry.Key));
        }

        Assert.DoesNotContain("key-010", keys);
        Assert.DoesNotContain("key-011", keys);
        Assert.DoesNotContain("key-024", keys);
        Assert.Contains("key-000", keys);
        Assert.Contains("key-039", keys);
    }
}
