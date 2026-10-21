using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Observability;

public sealed class PantsEngineMetricsParityTests
{
    [Fact]
    public async Task ShouldReportCompactionsRunPerEngineWhenTelemetryNotInitialized()
    {
        using var firstDirectory = new TemporaryDirectory();
        using var secondDirectory = new TemporaryDirectory();
        var firstOptions = PantsOpenOptions.Local(firstDirectory.Path).WithBackgroundCompaction(false);
        var secondOptions = PantsOpenOptions.Local(secondDirectory.Path).WithBackgroundCompaction(false);
        await using var first = await PantsDatabase.OpenAsync(firstOptions);
        await using var second = await PantsDatabase.OpenAsync(secondOptions);

        for (var round = 0; round < 3; round++)
        {
            await WriteAndFlushAsync(first, round);
            await WriteAndFlushAsync(second, round);
        }

        await first.Maintenance.CompactAllAsync();

        var firstMetrics = await first.Diagnostics.GetRuntimeMetricsAsync();
        var secondMetrics = await second.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(firstMetrics.CompactionsRun > 0);
        Assert.Equal(0, secondMetrics.CompactionsRun);
        Assert.True(firstMetrics.WalFsyncCount > 0);
        Assert.True(secondMetrics.WalFsyncCount > 0);

        await first.ShutdownAsync(TestTimeouts.Expected);
        await second.ShutdownAsync(TestTimeouts.Expected);
    }

    static async Task WriteAndFlushAsync(IPantsDatabase database, int round)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(
            TestBytes.FromString($"key-{round}"),
            TestBytes.FromString("value"));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }
}
