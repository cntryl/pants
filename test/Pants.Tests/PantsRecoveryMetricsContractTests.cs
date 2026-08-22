namespace Pants.Tests;

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
}
