using System.Diagnostics;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeCompactionNotificationRoutingParityTests
{
    const int GenerationCount = 4;
    const int KeyCount = 16;

    [Fact]
    public async Task ShouldCompleteManualCompactionWithoutFalseLateWorkerResponse()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path, backgroundCompaction: false);
        await using var database = await PantsDatabase.OpenAsync(options);
        await WriteGenerationsAsync(database);
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();

        await database.Maintenance.CompactAllAsync();
        var after = await database.Diagnostics.GetRuntimeMetricsAsync();

        Assert.True(after.CompactionsRun > before.CompactionsRun);
        await AssertLatestValuesAsync(database);
        await database.ShutdownAsync(TestTimeouts.Expected);
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertLatestValuesAsync(reopened);
        await reopened.ShutdownAsync(TestTimeouts.Expected);

        Assert.Equal(
            before.RuntimeAbandonedRequestsTotal,
            after.RuntimeAbandonedRequestsTotal);
        Assert.True(
            before.RuntimeLateResponsesTotal == after.RuntimeLateResponsesTotal,
            "A background compaction notification has no caller response obligation.");
    }

    [Fact]
    public async Task ShouldCompleteAutomaticCompactionWithoutFalseLateCallerResponse()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            CreateOptions(directory.Path, backgroundCompaction: true));
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();

        await WriteGenerationsAsync(database);
        var started = Stopwatch.GetTimestamp();
        PantsRuntimeMetrics after;
        while (true)
        {
            after = await database.Diagnostics.GetRuntimeMetricsAsync();
            if (after.CompactionsRun > before.CompactionsRun)
            {
                break;
            }

            Assert.True(
                Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10),
                $"Automatic compaction did not publish: {after}.");
            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }

        await AssertLatestValuesAsync(database);
        await database.ShutdownAsync(TestTimeouts.Expected);
        Assert.Equal(
            before.RuntimeAbandonedRequestsTotal,
            after.RuntimeAbandonedRequestsTotal);
        Assert.True(
            before.RuntimeLateResponsesTotal == after.RuntimeLateResponsesTotal,
            "Successful automatic compaction must not count as a late caller response.");
    }

    static PantsOpenOptions CreateOptions(string path, bool backgroundCompaction) =>
        PantsOpenOptions.Local(path)
            .WithBackgroundCompaction(backgroundCompaction);

    static async Task WriteGenerationsAsync(IPantsDatabase database)
    {
        var family = database.ColumnFamilies.DefaultFamily;
        for (var generation = 0; generation < GenerationCount; generation++)
        {
            await using (var transaction = await database.Transactions.BeginAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                for (var key = 0; key < KeyCount; key++)
                {
                    transaction.Put(
                        TestBytes.FromString($"key-{key:00}"),
                        TestBytes.FromString($"generation-{generation}"));
                }

                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }

            await database.Maintenance.FlushAsync(family);
        }
    }

    static async Task AssertLatestValuesAsync(IPantsDatabase database)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var key = 0; key < KeyCount; key++)
        {
            Assert.Equal(
                "generation-3",
                TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                    await transaction.GetAsync(TestBytes.FromString($"key-{key:00}")))));
        }
    }
}
