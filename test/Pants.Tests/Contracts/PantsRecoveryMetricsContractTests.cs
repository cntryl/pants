namespace Cntryl.Pants.Tests.Contracts;

public sealed class PantsRecoveryMetricsContractTests
{
    [Fact]
    public async Task ShouldExposeZeroRecoveryMetricsGivenFreshEngine()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));

        var recovery = await database.GetRecoveryMetricsAsync();

        Assert.Equal(0, recovery.WalRecordsReplayed);
        Assert.Equal(0, recovery.WalBytesReplayed);
        Assert.Equal(0, recovery.IntentLogReplayRuns);
        Assert.Equal(0, recovery.IntentLogEntriesReplayed);
    }

    [Fact]
    public async Task ShouldReportWalRecoveryWorkGivenBufferedCommitsBeforeReopen()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)))
        {
            for (var index = 0; index < 20; index++)
            {
                await using var transaction = await database.BeginTransactionAsync(
                    database.DefaultColumnFamily,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(
                    TestBytes.FromString($"recovery-key-{index}"),
                    TestBytes.FromString($"recovery-value-{index}"));
                await transaction.CommitAsync(PantsWriteOptions.Buffered);
            }
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        var recovery = await reopened.GetRecoveryMetricsAsync();

        Assert.True(recovery.WalRecordsReplayed > 0);
        Assert.True(recovery.WalBytesReplayed > 0);
        Assert.InRange(recovery.IntentLogReplayRuns, 0, 1);
    }

    [Fact]
    public async Task ShouldReportEveryPhysicalWalRecordGivenSpilledCommitBeforeReopen()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(64 * 1_024))
            .WithMemtableLimits(24 * 1_024)
            .WithTransactionMemoryPool(1_024);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            var value = new byte[900];
            for (var index = 0; index < 6; index++)
            {
                transaction.Put(TestBytes.FromString($"split-{index}"), value);
            }

            Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var recovery = await reopened.GetRecoveryMetricsAsync();
        var runtime = await reopened.GetRuntimeMetricsAsync();

        Assert.Equal(8, recovery.WalRecordsReplayed);
        Assert.True(recovery.WalBytesReplayed > 0);
        Assert.Equal(8, runtime.CurrentSequence);
        Assert.Equal(recovery.WalBytesReplayed, runtime.WalRecoveryBytesReplayed);
    }
}
