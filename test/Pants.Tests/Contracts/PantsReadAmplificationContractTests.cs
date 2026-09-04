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
            await FlushGenerationAsync(database, generation);
        }

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await reader.GetAsync("hot-key"u8.ToArray());
        Assert.NotNull(value);
        Assert.Equal("value-10", TestBytes.ToText(value.Value));

        var metrics = await database.Diagnostics.GetReadAmplificationMetricsAsync();
        Assert.Equal(1, metrics.ReadsTotal);
        Assert.Equal(11, metrics.SstsTouchedTotal);
        Assert.Equal(11, metrics.L0SstsTouchedTotal);
        Assert.Equal(22, metrics.BlocksReadTotal);
        Assert.Equal(1, metrics.SstBudgetViolationRate);
        Assert.Equal(1, metrics.BlockBudgetViolationRate);
        Assert.Equal(1, metrics.SstBudgetViolationsTotal);
        Assert.Equal(1, metrics.BlockBudgetViolationsTotal);
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
            await FlushGenerationAsync(database, generation);
        }

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("value-02", TestBytes.ToText(
            Assert.IsType<ReadOnlyMemory<byte>>(await reader.GetAsync("hot-key"u8.ToArray()))));

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

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var read = 0; read < 10; read++)
        {
            Assert.NotNull(await reader.GetAsync("hot-key"u8.ToArray()));
        }

        var metrics = await database.Diagnostics.GetReadAmplificationMetricsAsync();
        Assert.Equal(10, metrics.ReadsTotal);
        Assert.Equal(20, metrics.SstsTouchedTotal);
        Assert.Equal(20, metrics.L0SstsTouchedTotal);
        Assert.Equal(2, metrics.AverageSstsPerRead);
        Assert.Equal(2, metrics.AverageL0SstsPerRead);
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
            await FlushGenerationAsync(database, generation);
        }

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.NotNull(await reader.GetAsync("hot-key"u8.ToArray()));

        var amplification =
            await database.Diagnostics.GetReadAmplificationMetricsAsync();
        var runtime = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(1, amplification.SstBudgetViolationsTotal);
        Assert.Equal(1, amplification.CompactionTriggersTotal);
        Assert.Equal(1, runtime.ReadAmplificationCompactionTriggersTotal);
        Assert.Equal(1, runtime.CompactionsRun);
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
