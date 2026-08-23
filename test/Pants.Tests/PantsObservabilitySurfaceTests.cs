namespace Pants.Tests;

public sealed class PantsObservabilitySurfaceTests
{
    [Fact]
    public async Task ShouldExposeHealthyLocalRuntimeLayoutAndVerificationSurfaces()
    {
        using var directory = new TemporaryDirectory();
        PantsRuntimeMetrics metrics;
        PantsStorageLayout layout;
        PantsStorageVerificationReport online;
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)))
        {
            await using (var transaction = await database.BeginTransactionAsync(
                             database.DefaultColumnFamily,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Put("alpha"u8.ToArray(), "value-alpha"u8.ToArray());
                transaction.Put("bravo"u8.ToArray(), "value-bravo"u8.ToArray());
                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }

            await database.FlushAsync(database.DefaultColumnFamily);
            metrics = await database.GetRuntimeMetricsAsync();
            layout = await database.GetStorageLayoutAsync();
            online = await database.VerifyStorageAsync(TimeSpan.FromSeconds(5));
        }

        var offline = await PantsDatabase.VerifyPathAsync(directory.Path);

        Assert.Equal(PantsEngineHealth.Healthy, metrics.Health);
        Assert.True(metrics.SstCount >= 1);
        Assert.True(metrics.SstBytes > 0);
        Assert.True(metrics.ManifestLastPersistedSequence >= metrics.CurrentSequence);
        Assert.True(metrics.ManifestNextWalSequence > 0);
        Assert.Equal(0, metrics.MaximumMemtableWalSegmentGap);
        Assert.True(metrics.WalAppendCount >= metrics.WalFsyncCount);
        Assert.Equal(0, metrics.WalFlushCount);
        Assert.True(metrics.WalAppendNanosecondsTotal > 0);
        Assert.True(metrics.WalFsyncNanosecondsTotal > 0);
        Assert.True(metrics.WalFsyncNanosecondsMaximum > 0);
        Assert.True(metrics.FlushBuildCount >= 1);
        Assert.True(metrics.FlushPublishCount >= 1);
        Assert.True(metrics.FlushEnqueuedTotal >= metrics.FlushBuildCount);
        Assert.True(metrics.FlushBuildNanosecondsTotal >= metrics.FlushBuildNanosecondsMaximum);
        Assert.True(metrics.FlushPublishNanosecondsTotal >= metrics.FlushPublishNanosecondsMaximum);
        Assert.True(metrics.FlushBuildNanosecondsMaximum > 0);
        Assert.True(metrics.FlushPublishNanosecondsMaximum > 0);
        Assert.Equal(0, metrics.FlushQueueDepth);
        Assert.Equal(0, metrics.FlushInFlight);
        Assert.Equal(0, metrics.FlushFailuresTotal);
        Assert.Equal(0, metrics.FlushRetriesTotal);
        Assert.Equal(0, metrics.CacheHits + metrics.CacheMisses);
        Assert.Equal(0, metrics.CloudAsyncWalUploadsFailed);
        Assert.Equal(0, metrics.HybridPendingEvictions);
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "wal"),
            "*.wal",
            SearchOption.TopDirectoryOnly));

        Assert.Equal(PantsEngineHealth.Healthy, layout.Health);
        var files = layout.Levels.SelectMany(static level => level.Files).ToArray();
        Assert.NotEmpty(files);
        Assert.All(files, static file =>
        {
            Assert.NotNull(file.SmallestKey);
            Assert.NotNull(file.LargestKey);
            Assert.NotNull(file.SmallestSequence);
            Assert.NotNull(file.LargestSequence);
            Assert.True(file.SizeBytes > 0);
        });
        Assert.True(online.ManifestFilesVerified >= 1);
        Assert.True(online.SstFilesVerified >= 1);
        Assert.Equal(PantsEngineHealth.Healthy, online.Health);
        Assert.True(offline.ManifestFilesVerified >= 1);
        Assert.Equal(PantsEngineHealth.Healthy, offline.Health);
    }
}
