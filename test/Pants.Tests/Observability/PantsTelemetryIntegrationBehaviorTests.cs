namespace Pants.Tests;

public sealed class PantsTelemetryIntegrationBehaviorTests
{
    [Theory]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldPreserveAllValuesGivenRepeatedReadsWhenValuesAccessedRepeatedly(
        string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(mode, directory.Path);
        var family = await database.CreateColumnFamilyAsync("test");
        var records = CreateRecords("metrics_read_key_", 0, 50, _ => "metric_value"u8.ToArray());
        await WriteBatchAsync(database, family, mode, records, flush: true);

        await AssertRecordsAsync(database, family, records);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldPreserveAllWrittenValuesGivenLargeWriteBatchWhenWritten(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(mode, directory.Path);
        var family = await database.CreateColumnFamilyAsync("test");
        var records = CreateRecords(
            "metrics_write_key_",
            0,
            100,
            _ => "metric_write_value"u8.ToArray());

        await WriteBatchAsync(database, family, mode, records, flush: false);

        await AssertRecordsAsync(database, family, records);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldPreserveAllValuesGivenCompactionWhenRequested(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(mode, directory.Path);
        var family = await database.CreateColumnFamilyAsync("test");
        var first = CreateRecords("compact_metric_key_", 0, 100, _ => "gen1"u8.ToArray());
        var second = CreateRecords("compact_metric_key_", 100, 100, _ => "gen2"u8.ToArray());
        await WriteBatchAsync(database, family, mode, first, flush: true);
        await WriteBatchAsync(database, family, mode, second, flush: true);

        await database.CompactAllAsync();

        await AssertRecordsAsync(database, family, first.Concat(second));
    }

    [Theory]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldPreserveRepeatedReadsGivenCompletedCacheWarmupWhenReadsRepeated(
        string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(mode, directory.Path);
        var family = await database.CreateColumnFamilyAsync("test");
        var records = CreateRecords("cache_metric_key_", 0, 100, _ => "cached_value"u8.ToArray());
        await WriteBatchAsync(database, family, mode, records, flush: true);
        var warmupRecords = records[..50];
        await AssertRecordsAsync(database, family, warmupRecords);
        var afterWarmup = await database.GetReadPathDiagnosticsAsync();

        await AssertRecordsAsync(database, family, warmupRecords);

        var afterSecondPass = await database.GetReadPathDiagnosticsAsync();
        Assert.True(afterSecondPass.SstBlockCacheHits > afterWarmup.SstBlockCacheHits);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldPreserveLargeValuesGivenWalBackedWriteBatchWhenFlushed(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(mode, directory.Path);
        var family = await database.CreateColumnFamilyAsync("test");
        var records = CreateRecords(
            "wal_metric_key_",
            0,
            100,
            _ => Enumerable.Repeat((byte)'W', 1024).ToArray());

        await WriteBatchAsync(database, family, mode, records, flush: true);

        await AssertRecordsAsync(database, family, [records[0], records[^1]]);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldPreserveExistingDataGivenPlaceholderResetWhenNewWriteAdded(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(mode, directory.Path);
        var family = await database.CreateColumnFamilyAsync("test");
        var initial = CreateRecords("reset_metric_key_", 0, 50, _ => "value"u8.ToArray());
        await WriteBatchAsync(database, family, mode, initial, flush: false);
        var followUp = new KeyValuePair<byte[], byte[]>(
            "single_key"u8.ToArray(),
            "single_value"u8.ToArray());

        await WriteBatchAsync(database, family, mode, [followUp], flush: false);

        await AssertRecordsAsync(database, family, initial.Append(followUp));
    }

    static PantsOpenOptions Options(string mode, string path) => mode switch
    {
        "local" => PantsOpenOptions.Local(path).WithBackgroundCompaction(false),
        "cloud" => PantsOpenOptions
            .SimulatedCloud(path, "pants-tests", "telemetry-integration/")
            .WithBackgroundCompaction(false),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown storage mode.")
    };

    static ValueTask<IPantsDatabase> OpenAsync(string mode, string path) =>
        PantsDatabase.OpenAsync(Options(mode, path));

    static PantsWriteOptions WriteOptions(string mode) =>
        mode == "cloud" ? PantsWriteOptions.CloudAsync : PantsWriteOptions.Buffered;

    static KeyValuePair<byte[], byte[]>[] CreateRecords(
        string keyPrefix,
        int start,
        int count,
        Func<int, byte[]> valueFactory) =>
        Enumerable.Range(start, count)
            .Select(index => new KeyValuePair<byte[], byte[]>(
                TestBytes.FromString($"{keyPrefix}{index:0000}"),
                valueFactory(index)))
            .ToArray();

    static async ValueTask WriteBatchAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string mode,
        IEnumerable<KeyValuePair<byte[], byte[]>> records,
        bool flush)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        foreach (var record in records)
        {
            transaction.Put(record.Key, record.Value);
        }

        await transaction.CommitAsync(WriteOptions(mode));
        if (flush)
        {
            await database.FlushAsync(family);
        }
    }

    static async ValueTask AssertRecordsAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        IEnumerable<KeyValuePair<byte[], byte[]>> records)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);
        foreach (var record in records)
        {
            var value = await transaction.GetAsync(record.Key);
            Assert.NotNull(value);
            Assert.Equal(record.Value, value.Value.ToArray());
        }
    }
}
