namespace Pants.Tests;

public sealed class PantsWalIntegrationBehaviorTests
{
    [Fact]
    public async Task ShouldRecoverDataFromWalAfterFlushAndReopen()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            var family = await database.CreateColumnFamilyAsync("test");
            for (var index = 0; index < 50; index++)
            {
                await CommitAsync(
                    database,
                    family,
                    transaction => transaction.Put(
                        TestBytes.FromString($"wal_key_{index:0000}"),
                        "wal_value"u8.ToArray()));
            }

            await database.FlushAsync(family);
        }

        await using var reopened = await OpenAsync(directory.Path);
        var reopenedFamily = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("test"));

        Assert.Equal(50, (await ScanAsync(reopened, reopenedFamily)).Count);
    }

    [Fact]
    public async Task ShouldHandleLargeValueInWalAcrossFlushAndReopen()
    {
        using var directory = new TemporaryDirectory();
        var expected = Enumerable.Repeat(byte.MaxValue, 1_000_000).ToArray();
        await using (var database = await OpenAsync(directory.Path))
        {
            var family = await database.CreateColumnFamilyAsync("test");
            await CommitAsync(
                database,
                family,
                transaction => transaction.Put("large_wal_key"u8.ToArray(), expected));
            await database.FlushAsync(family);
        }

        await using var reopened = await OpenAsync(directory.Path);
        var reopenedFamily = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("test"));

        Assert.Equal(expected, (await GetAsync(reopened, reopenedFamily, "large_wal_key"))?.ToArray());
    }

    [Fact]
    public async Task ShouldRecoverMixedOperationsFromWalAfterFlushAndReopen()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            var family = await database.CreateColumnFamilyAsync("test");
            await CommitAsync(
                database,
                family,
                transaction => transaction.Put("put_key"u8.ToArray(), "put_value"u8.ToArray()));
            await CommitAsync(
                database,
                family,
                transaction => transaction.Delete("put_key"u8.ToArray()));
            await CommitAsync(
                database,
                family,
                transaction => transaction.Put("put_key"u8.ToArray(), "put_value_v2"u8.ToArray()));
            for (var index = 0; index < 20; index++)
            {
                await CommitAsync(
                    database,
                    family,
                    transaction => transaction.Put(
                        TestBytes.FromString($"dr_{index:00}"),
                        "v"u8.ToArray()));
            }

            await CommitAsync(
                database,
                family,
                transaction => transaction.DeleteRange(
                    "dr_05"u8.ToArray(),
                    "dr_15"u8.ToArray()));
            await database.FlushAsync(family);
        }

        await using var reopened = await OpenAsync(directory.Path);
        var reopenedFamily = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("test"));
        var rows = await ScanAsync(reopened, reopenedFamily);

        Assert.Equal(
            "put_value_v2",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await GetAsync(reopened, reopenedFamily, "put_key"))));
        Assert.DoesNotContain(rows, static row =>
        {
            var key = TestBytes.ToText(row.Key);
            return string.CompareOrdinal(key, "dr_05") >= 0 &&
                string.CompareOrdinal(key, "dr_15") < 0;
        });
        Assert.Contains(rows, static row => TestBytes.ToText(row.Key) == "dr_04");
        Assert.Contains(rows, static row => TestBytes.ToText(row.Key) == "dr_15");
    }

    static ValueTask<IPantsDatabase> OpenAsync(string path) =>
        PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(path).WithBackgroundCompaction(false));

    static async ValueTask CommitAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        Action<IPantsTransaction> stage)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        stage(transaction);
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
    }

    static async ValueTask<ReadOnlyMemory<byte>?> GetAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);
        return await transaction.GetAsync(TestBytes.FromString(key));
    }

    static async ValueTask<IReadOnlyList<PantsEntry>> ScanAsync(
        IPantsDatabase database,
        IPantsColumnFamily family)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        var rows = new List<PantsEntry>();
        await foreach (var row in scan)
        {
            rows.Add(row);
        }

        return rows;
    }
}
