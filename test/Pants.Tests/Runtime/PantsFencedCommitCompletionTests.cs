using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsFencedCommitCompletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldCompleteReadOnlyAndStatelessCommitsButFenceDurabilityEffectsAfterApplyFailure(bool cloud)
    {
        using var directory = new TemporaryDirectory();
        var options = (cloud
                ? PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "fenced-noop/")
                : PantsOpenOptions.Local(directory.Path))
            .WithBackgroundCompaction(false)
            .WithCloudWritePolicy(new PantsCloudWritePolicy(long.MaxValue, long.MaxValue, TimeSpan.FromHours(1), int.MaxValue));
        var failpoints = new ArmableFailpointHandler();
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(failpoints)))
        {
            await using var retained = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
            await using var rolledBack = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            rolledBack.Put("rolled-back"u8.ToArray(), "discard"u8.ToArray());
            var disposed = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            disposed.Put("disposed"u8.ToArray(), "discard"u8.ToArray());
            await using (var write = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
            {
                write.Put("accepted"u8.ToArray(), "durable-wal"u8.ToArray());
                failpoints.Arm(Failpoint.BeforeCommitApply);
                await Assert.ThrowsAsync<PantsCommitOutcomeUnknownException>(() => write.CommitAsync(
                    cloud ? PantsWriteOptions.CloudAsync : PantsWriteOptions.Sync).AsTask());
            }

            Assert.Null(await retained.GetAsync("accepted"u8.ToArray()));
            await retained.RollbackAsync();
            await rolledBack.RollbackAsync();
            await disposed.DisposeAsync();
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(0, before.ActiveSnapshots);
            await using (var readOnly = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly))
            {
                Assert.Null(await readOnly.GetAsync("accepted"u8.ToArray()));
                await readOnly.CommitAsync(cloud ? PantsWriteOptions.CloudStrict : PantsWriteOptions.Sync);
            }

            await using (var stateless = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
            {
                await stateless.CommitAsync(cloud ? PantsWriteOptions.CloudAsync : PantsWriteOptions.Buffered);
            }

            await using (var barrier = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
            {
                if (cloud)
                {
                    await barrier.CommitAsync(PantsWriteOptions.CloudStrict);
                }
                else
                {
                    await Assert.ThrowsAsync<PantsFencedException>(() => barrier.CommitAsync(PantsWriteOptions.Sync).AsTask());
                }
            }
            await using (var guarded = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
            {
                guarded.AssertValue("absent"u8.ToArray(), null);
                await Assert.ThrowsAsync<PantsFencedException>(() => guarded.CommitAsync(
                    cloud ? PantsWriteOptions.CloudStrict : PantsWriteOptions.Sync).AsTask());
            }

            await Assert.ThrowsAsync<PantsFencedException>(() => database.Maintenance
                .FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask());
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(before.CurrentSequence, after.CurrentSequence);
            Assert.Equal(before.WalAppendCount, after.WalAppendCount);
            Assert.Equal(before.WalFsyncCount, after.WalFsyncCount);
            Assert.Equal(0, after.ActiveSnapshots);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("durable-wal"u8.ToArray(), (await reader.GetAsync("accepted"u8.ToArray()))?.ToArray());
        Assert.Null(await reader.GetAsync("rolled-back"u8.ToArray()));
        Assert.Null(await reader.GetAsync("disposed"u8.ToArray()));
    }
}
