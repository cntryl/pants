using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsWriteBackpressureBehaviorTests
{
    [Fact]
    public async Task ShouldSlowOneSubmissionInThePublishedL0SoftBandAndRecoverItsValue()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)
            .WithWriteBackpressure(new PantsWriteBackpressureConfiguration(
                L0SoftLimit: 1, MaximumAdmissionDelay: TimeSpan.FromMilliseconds(20)));
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await PutAsync(database, "seed");
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();

            await PutAsync(database, "after");

            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(before.WriteSlowdownCount + 1, after.WriteSlowdownCount);
            Assert.True(after.WriteSlowdownNanosecondsTotal > before.WriteSlowdownNanosecondsTotal);
            Assert.False(after.WriteStalled);
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertValueAsync(reopened, "after", true);
    }

    [Fact]
    public async Task ShouldRejectPendingDebtBeforeWalAllocationThenCompactRetryAndRecover()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 4, BackgroundEnabled: false))
            .WithWriteBackpressure(new PantsWriteBackpressureConfiguration(PendingCompactionHardLimitBytes: 2));
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            for (var index = 0; index < 4; index++)
            {
                await PutAsync(database, $"seed-{index}");
                await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            }
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();

            var rejection = await Assert.ThrowsAsync<PantsWriteStallException>(() => PutAsync(database, "refused"));

            Assert.Contains("pending compaction", rejection.Message, StringComparison.OrdinalIgnoreCase);
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.True(after.EstimatedPendingCompactionBytes >= 2);
            Assert.True(after.WriteStalled);
            Assert.Equal(before.CurrentSequence, after.CurrentSequence);
            Assert.Equal(before.ManifestNextWalSequence, after.ManifestNextWalSequence);
            Assert.Equal(before.WalAppendCount, after.WalAppendCount);
            Assert.Equal(before.WalBytesWrittenTotal, after.WalBytesWrittenTotal);
            Assert.False(await database.Maintenance.WaitForWriteStallClearAsync(
                database.ColumnFamilies.DefaultFamily, TimeSpan.Zero));
            await AssertValueAsync(database, "refused", false);
            await using (var empty = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                await empty.CommitAsync(PantsWriteOptions.Buffered);
            }
            await database.Maintenance.CompactAllAsync();
            Assert.True(await database.Maintenance.WaitForWriteStallClearAsync(
                database.ColumnFamilies.DefaultFamily, TimeSpan.Zero));
            await PutAsync(database, "refused");
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertValueAsync(reopened, "refused", true);
        for (var index = 0; index < 4; index++)
        {
            await AssertValueAsync(reopened, $"seed-{index}", true);
        }
    }

    [Fact]
    public async Task ShouldChargeActualEncodedMaintenanceOutputToTheSharedRateOwner()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)
            .WithWriteBackpressure(new PantsWriteBackpressureConfiguration(BackgroundWriteBytesPerSecond: 1024 * 1024));
        await using var database = await PantsDatabase.OpenAsync(options);
        await PutAsync(database, "seed", forceRewrite: true);

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        var files = (await database.Diagnostics.GetStorageLayoutAsync()).Levels.SelectMany(static level => level.Files);
        var bytes = files.Sum(static file => file.SizeBytes);
        Assert.True(bytes > 0);
        Assert.Equal(checked((ulong)bytes), metrics.BackgroundRateLimitedBytes);
        Assert.Equal(0, metrics.WriteSlowdownCount);
        await PutAsync(database, "second", forceRewrite: true);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var beforeCompaction = await database.Diagnostics.GetRuntimeMetricsAsync();

        await database.Maintenance.CompactAllAsync();

        var compactedFiles = (await database.Diagnostics.GetStorageLayoutAsync()).Levels
            .SelectMany(static level => level.Files).ToArray();
        Assert.All(compactedFiles, static file => Assert.True(file.Level > 0));
        var afterCompaction = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(checked((ulong)compactedFiles.Sum(static file => file.SizeBytes)),
            afterCompaction.BackgroundRateLimitedBytes - beforeCompaction.BackgroundRateLimitedBytes);
    }

    [Fact]
    public async Task ShouldRejectASoftLimitWithoutAPositiveDelayBeforeOpeningStorage()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithWriteBackpressure(new PantsWriteBackpressureConfiguration(L0SoftLimit: 1));

        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() => PantsDatabase.OpenAsync(options).AsTask());

        Assert.False(File.Exists(Path.Combine(directory.Path, "FORMAT")));
    }

    static async Task PutAsync(IPantsDatabase database, string key, bool forceRewrite = false)
    {
        await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), "value"u8.ToArray());
        if (forceRewrite)
        {
            // The test observes physical output; overlapping empty keyspace prevents promotion.
            transaction.DeleteRange("!overlap/a"u8.ToArray(), "!overlap/z"u8.ToArray());
        }
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async Task AssertValueAsync(IPantsDatabase database, string key, bool present)
    {
        await using var read = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(present ? "value"u8.ToArray() : null, (await read.GetAsync(TestBytes.FromString(key)))?.ToArray());
    }
}
