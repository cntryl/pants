using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsHotSstTrackingTests
{
    [Fact]
    public async Task ShouldPreserveReadsGivenTwoFlushedBatchesWhenRepeatedlyAccessed()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        for (var index = 0; index < 10; index++)
        {
            await PutAsync(database, $"batch1_key{index:000}", "value1");
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        for (var index = 0; index < 10; index++)
        {
            await PutAsync(database, $"batch2_key{index:000}", "value2");
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        for (var read = 0; read < 5; read++)
        {
            Assert.Equal("value1", await ReadAsync(database, "batch1_key000"));
        }

        Assert.Equal("value2", await ReadAsync(database, "batch2_key000"));
    }

    [Fact]
    public async Task ShouldReturnLatestValueGivenOverlappingL0KeysWhenMultipleBatchesFlushed()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        for (var batch = 0; batch < 3; batch++)
        {
            for (var index = 0; index < 5; index++)
            {
                await PutAsync(database, $"key{index:000}", $"value_batch{batch}");
            }

            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        Assert.Equal("value_batch2", await ReadAsync(database, "key000"));
        Assert.Equal("value_batch2", await ReadAsync(database, "key004"));
    }

    [Fact]
    public async Task ShouldFindKeysGivenDisjointKeyRangesWhenFlushed()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        foreach (var (prefix, value) in new[]
                 {
                     ("a", "value_a"),
                     ("b", "value_b"),
                     ("c", "value_c")
                 })
        {
            for (var index = 0; index < 10; index++)
            {
                await PutAsync(database, $"{prefix}{index:000}", value);
            }

            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        Assert.Equal("value_b", await ReadAsync(database, "b005"));
        Assert.Equal("value_a", await ReadAsync(database, "a005"));
        Assert.Equal("value_c", await ReadAsync(database, "c005"));
    }

    [Fact]
    public async Task ShouldPreserveReadabilityGivenMixedAccessPatternWhenKeysRepeatedlyAccessed()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        for (var index = 0; index < 20; index++)
        {
            await PutAsync(database, $"key{index:000}", "test_value");
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        for (var read = 0; read < 10; read++)
        {
            Assert.Equal("test_value", await ReadAsync(database, "key000"));
        }

        for (var read = 0; read < 3; read++)
        {
            Assert.Equal("test_value", await ReadAsync(database, "key010"));
        }

        Assert.Equal("test_value", await ReadAsync(database, "key019"));
        Assert.Null(await ReadAsync(database, "missing_key"));
    }

    static ValueTask<IPantsDatabase> OpenAsync(string path) =>
        PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(path).WithBackgroundCompaction(false));

    static async Task PutAsync(IPantsDatabase database, string key, string value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
    }

    static async Task<string?> ReadAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(TestBytes.FromString(key));
        return value is { } present ? TestBytes.ToText(present) : null;
    }
}
