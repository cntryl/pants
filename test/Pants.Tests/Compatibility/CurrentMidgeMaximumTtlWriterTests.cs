using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class CurrentMidgeMaximumTtlWriterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldKeepMidgeMaximumExpiryVisibleAtTheMaximumRepresentableClock(bool exactMaximum)
    {
        using var directory = new TemporaryDirectory();
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compatibility", "Midge",
            "c684310d9d8ce8ecd1151c47151d4bea98b348cf", "ttl-maximum-midge0");
        foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(directory.Path, Path.GetRelativePath(fixture, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        var clock = new ManualClock(exactMaximum ? DateTimeOffset.MaxValue : DateTimeOffset.MaxValue.AddTicks(-1));
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false).WithTtlClock(clock);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await AssertValueAsync(database);
            await using (var writer = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
            {
                writer.Put("pants"u8.ToArray(), "extended"u8.ToArray());
                await writer.CommitAsync(PantsWriteOptions.Sync);
            }
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await database.Maintenance.CompactAllAsync();
            await AssertValueAsync(database);
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertValueAsync(reopened);
    }

    static async Task AssertValueAsync(IPantsDatabase database)
    {
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("still-future"u8.ToArray(), (await reader.GetAsync("ttl-row"u8.ToArray()))?.ToArray());
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await foreach (var entry in scan)
        {
            values.Add(TestBytes.ToText(entry.Key), entry.Value.ToArray());
        }
        Assert.Equal("still-future"u8.ToArray(), values["ttl-row"]);
    }
}
