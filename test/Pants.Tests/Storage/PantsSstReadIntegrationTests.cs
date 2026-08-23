namespace Cntryl.Pants.Tests.Storage;

public sealed class PantsSstReadIntegrationTests
{
    [Fact]
    public async Task ShouldReadFromSstAfterFlush()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        for (var index = 0; index < 10; index++)
        {
            await PutAsync(database, $"key_{index:000}", "value_from_sst");
        }

        await database.FlushAsync(database.DefaultColumnFamily);

        Assert.Equal("value_from_sst", await ReadAsync(database, "key_000"));
        Assert.Equal("value_from_sst", await ReadAsync(database, "key_005"));
        Assert.Null(await ReadAsync(database, "missing_key"));
    }

    [Fact]
    public async Task ShouldTrackL0SstReads()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        for (var batch = 0; batch < 3; batch++)
        {
            for (var index = 0; index < 5; index++)
            {
                await PutAsync(database, $"batch{batch}_key{index}", "value");
            }

            await database.FlushAsync(database.DefaultColumnFamily);
        }

        for (var batch = 0; batch < 3; batch++)
        {
            Assert.Equal("value", await ReadAsync(database, $"batch{batch}_key0"));
        }

        var metrics = await database.GetReadAmplificationMetricsAsync();
        Assert.True(metrics.L0SstsTouchedTotal >= 3);
    }

    [Fact]
    public async Task ShouldUseKeyRangesForHigherLevels()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        for (var index = 0; index < 20; index++)
        {
            await PutAsync(database, $"key_{index:000}", "test_value");
        }

        await database.FlushAsync(database.DefaultColumnFamily);

        Assert.Equal("test_value", await ReadAsync(database, "key_000"));
        Assert.Equal("test_value", await ReadAsync(database, "key_010"));
        Assert.Equal("test_value", await ReadAsync(database, "key_019"));
        Assert.Null(await ReadAsync(database, "key_999"));
        Assert.True((await database.GetReadAmplificationMetricsAsync()).KeyRangeRejectsTotal > 0);
    }

    [Fact]
    public async Task ShouldHandleMemtableAndSstReads()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await PutAsync(database, "sst_key", "sst_value");
        await database.FlushAsync(database.DefaultColumnFamily);
        await PutAsync(database, "mem_key", "mem_value");

        Assert.Equal("sst_value", await ReadAsync(database, "sst_key"));
        Assert.Equal("mem_value", await ReadAsync(database, "mem_key"));

        await PutAsync(database, "sst_key", "updated_value");

        Assert.Equal("updated_value", await ReadAsync(database, "sst_key"));
    }

    [Fact]
    public async Task ShouldErrorGivenCorruptSstWhenTransactionGetReadsFlushedKey()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await PutAsync(database, "corrupt-key", "corrupt-value");
        await database.FlushAsync(database.DefaultColumnFamily);
        CorruptFirstSstDataByte(directory.Path);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        await Assert.ThrowsAsync<StorageException>(() =>
            transaction.GetAsync("corrupt-key"u8.ToArray()).AsTask());
    }

    [Fact]
    public async Task ShouldErrorGivenCorruptSstWhenTransactionScanReadsFlushedRange()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        for (var index = 0; index < 3; index++)
        {
            await PutAsync(database, $"corrupt-range-{index}", "corrupt-value");
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        CorruptFirstSstDataByte(directory.Path);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());

        await Assert.ThrowsAsync<StorageException>(async () =>
        {
            await foreach (var _ in scan)
            {
            }
        });
    }

    static ValueTask<IPantsDatabase> OpenAsync(string path) =>
        PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(path).WithBackgroundCompaction(false));

    static async Task PutAsync(IPantsDatabase database, string key, string value)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
    }

    static async Task<string?> ReadAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(TestBytes.FromString(key));
        return value is { } present ? TestBytes.ToText(present) : null;
    }

    static void CorruptFirstSstDataByte(string databasePath)
    {
        var path = Directory.EnumerateFiles(
                Path.Combine(databasePath, "sst"),
                "*.sst",
                SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .First();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);
        stream.Position = sizeof(uint);
        var value = stream.ReadByte();
        Assert.NotEqual(-1, value);
        stream.Position = sizeof(uint);
        stream.WriteByte(checked((byte)(value ^ 1)));
        stream.Flush(true);
    }
}
