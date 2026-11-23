using System.Diagnostics;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Contracts;

public sealed class PantsReadAmplificationContractTests
{
    [Fact]
    public async Task ShouldShowZeroReadAmplificationMetricsForFreshEngine()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));

        var metrics = await database.Diagnostics.GetReadAmplificationMetricsAsync();

        Assert.Equal(new PantsReadAmplificationMetrics(), metrics);
    }

    [Fact]
    public async Task ShouldExposeExactMetricsGivenRepeatedLocalSstReads()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        await FlushGenerationAsync(database, 0);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        for (var read = 0; read < 5; read++)
        {
            var value = await reader.GetAsync("hot-key"u8.ToArray());
            Assert.NotNull(value);
            Assert.Equal("value-00", TestBytes.ToText(value.Value));
        }

        var metrics = await database.Diagnostics.GetReadAmplificationMetricsAsync();
        Assert.Equal(5, metrics.ReadsTotal);
        Assert.Equal(5, metrics.SstsTouchedTotal);
        Assert.Equal(5, metrics.L0SstsTouchedTotal);
        Assert.Equal(10, metrics.BlocksReadTotal);
        Assert.Equal(1, metrics.AverageSstsPerRead);
        Assert.Equal(1, metrics.AverageL0SstsPerRead);
        Assert.Equal(2, metrics.AverageBlocksPerRead);
        Assert.Equal(1, metrics.L0OverlapRate);
        Assert.Equal(0, metrics.SstBudgetViolationRate);
        Assert.Equal(0, metrics.BlockBudgetViolationRate);
    }

    [Fact]
    public async Task ShouldReportBudgetViolationGivenElevenOverlappingSsts()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));

        for (var generation = 0; generation < 11; generation++)
        {
            await FlushBookendedGenerationAsync(database, generation);
        }

        // Every file spans the key but none holds it, so no source decides the read early.

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("hot-key-m"u8.ToArray()));

        var metrics = await database.Diagnostics.GetReadAmplificationMetricsAsync();
        Assert.Equal(1, metrics.ReadsTotal);
        Assert.Equal(11, metrics.SstsTouchedTotal);
        Assert.Equal(11, metrics.L0SstsTouchedTotal);
        Assert.Equal(11, metrics.BlocksReadTotal);
        Assert.Equal(1, metrics.SstBudgetViolationRate);
        Assert.Equal(0, metrics.BlockBudgetViolationRate);
        Assert.Equal(1, metrics.SstBudgetViolationsTotal);
        Assert.Equal(0, metrics.BlockBudgetViolationsTotal);
        Assert.Equal(0, metrics.CompactionTriggersTotal);
    }

    [Fact]
    public async Task ShouldTrackExactL0OverlapAcrossOverlappingSsts()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));

        for (var generation = 0; generation < 3; generation++)
        {
            await FlushBookendedGenerationAsync(database, generation);
        }


        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("hot-key-m"u8.ToArray()));

        var metrics = await database.Diagnostics.GetReadAmplificationMetricsAsync();
        Assert.Equal(1, metrics.ReadsTotal);
        Assert.Equal(3, metrics.SstsTouchedTotal);
        Assert.Equal(3, metrics.L0SstsTouchedTotal);
        Assert.Equal(3, metrics.AverageSstsPerRead);
        Assert.Equal(3, metrics.AverageL0SstsPerRead);
        Assert.Equal(1, metrics.L0OverlapRate);
    }

    [Fact]
    public async Task ShouldAccumulateExactAverageAcrossMultipleLocalSstReads()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));

        for (var generation = 0; generation < 2; generation++)
        {
            await FlushGenerationAsync(database, generation);
        }

        // The newest L0 file decides every read, so each read touches exactly one SST.

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        for (var read = 0; read < 10; read++)
        {
            Assert.NotNull(await reader.GetAsync("hot-key"u8.ToArray()));
        }


        var metrics = await database.Diagnostics.GetReadAmplificationMetricsAsync();
        Assert.Equal(10, metrics.ReadsTotal);
        Assert.Equal(10, metrics.SstsTouchedTotal);
        Assert.Equal(10, metrics.L0SstsTouchedTotal);
        Assert.Equal(1, metrics.AverageSstsPerRead);
        Assert.Equal(1, metrics.AverageL0SstsPerRead);
    }

    [Fact]
    public async Task ShouldTriggerCompactionGivenReadExceedsSstBudget()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithPerformanceGoal(PantsPerformanceGoal.Throughput)
                .WithWorkloadProfile(PantsWorkloadProfile.WriteHeavy));

        for (var generation = 0; generation < 6; generation++)
        {
            await FlushBookendedGenerationAsync(database, generation);
        }


        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("hot-key-m"u8.ToArray()));

        var amplification =
            await database.Diagnostics.GetReadAmplificationMetricsAsync();
        var runtime = await WaitForRuntimeMetricsAsync(
            database,
            static metrics => metrics.CompactionsRun >= 1);
        Assert.Equal(1, amplification.SstBudgetViolationsTotal);
        Assert.Equal(1, amplification.CompactionTriggersTotal);
        Assert.Equal(1, runtime.ReadAmplificationCompactionTriggersTotal);
        Assert.Equal(1, runtime.CompactionsRun);
    }

    static async ValueTask<PantsRuntimeMetrics> WaitForRuntimeMetricsAsync(
        IPantsDatabase database,
        Func<PantsRuntimeMetrics, bool> predicate
    )
    {
        var started = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) < TestTimeouts.Expected)
        {
            var metrics = await database.Diagnostics.GetRuntimeMetricsAsync()
                .AsTask()
                .WaitAsync(TestTimeouts.Expected);

            if (predicate(metrics))
            {
                return metrics;
            }


            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }


        throw new TimeoutException("Read-amplification compaction did not complete.");
    }

    /// <summary>
    ///     Flushes a file whose key range spans <c>hot-key-m</c> without holding it, so a read of
    ///     that key cannot be decided by any single source and visits every candidate file.
    /// </summary>
    static async Task FlushBookendedGenerationAsync(IPantsDatabase database, int generation)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        var value = TestBytes.FromString($"value-{generation:D2}");
        transaction.Put("hot-key-a"u8.ToArray(), value);
        transaction.Put("hot-key-z"u8.ToArray(), value);
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }

    static async Task FlushGenerationAsync(IPantsDatabase database, int generation)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(
            "hot-key"u8.ToArray(),
            TestBytes.FromString($"value-{generation:D2}"));
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }
}
