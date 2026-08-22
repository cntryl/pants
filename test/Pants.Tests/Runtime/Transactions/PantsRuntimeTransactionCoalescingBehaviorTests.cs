namespace Pants.Tests;

public sealed class PantsRuntimeTransactionCoalescingBehaviorTests
{
    [Fact]
    public async Task ShouldPreserveEveryConcurrentWriteWhileRuntimeCoalesces()
    {
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.InMemory().WithMemtableLimits(64L * 1024 * 1024));
        var family = await database.CreateColumnFamilyAsync("test_cf");
        const int writerCount = 8;
        const int operationsPerWriter = 500;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = Enumerable.Range(0, writerCount)
            .Select(writer => WriteSeriesAsync(
                database,
                family,
                writer,
                operationsPerWriter,
                start.Task))
            .ToArray();

        start.SetResult();
        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(20));

        var metrics = await database.GetRuntimeMetricsAsync();
        await using var reader = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var rowCount = 0;
        await foreach (var _ in scan)
        {
            rowCount++;
        }

        var totalOperations = writerCount * operationsPerWriter;
        Assert.Equal(totalOperations, rowCount);
        Assert.True(metrics.WalAppendCount < totalOperations);
    }

    [Fact]
    public async Task ShouldMaintainOrderingAcrossSequentialRuntimeCommits()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        var family = await database.CreateColumnFamilyAsync("test_counter");
        const int operationCount = 100;
        for (var index = 0; index < operationCount; index++)
        {
            await CommitAsync(database, family, "counter", index.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }

        await using var reader = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);
        var finalValue = await reader.GetAsync("counter"u8.ToArray());

        Assert.Equal("99", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(finalValue)));
    }

    static async Task WriteSeriesAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        int writer,
        int operationCount,
        Task start)
    {
        await start;
        for (var operation = 0; operation < operationCount; operation++)
        {
            await CommitAsync(
                database,
                family,
                $"key-t{writer:00}-o{operation:000000}",
                $"val-{operation}");
        }
    }

    static async Task CommitAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key,
        string value)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
    }
}
