namespace Cntryl.Pants.Tests;

public sealed class PantsCloudRecoveryTests
{
    [Fact]
    public async Task ShouldPreserveFlushedValuesWhenReopeningAfterShortUploadWindow()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);

        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = await database.CreateColumnFamilyAsync("test");
            await WriteBatchAsync(database, family, 0, 100, "partial_upload_key", "value_before_upload");
            await database.FlushAsync(family);
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        await using (var reopened = await PantsDatabase.OpenAsync(options))
        {
            var family = await reopened.CreateColumnFamilyAsync("test");
            await AssertBatchAsync(
                reopened,
                family,
                0,
                100,
                "partial_upload_key",
                static _ => "value_before_upload");
        }

        Assert.False(Directory.Exists(Path.Combine(directory.Path, "cloud_recovery")));
    }

    [Fact]
    public async Task ShouldPreserveBothFlushedBatchesWhenReopeningAfterCompactionRequest()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);

        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = await database.CreateColumnFamilyAsync("test");
            await WriteBatchAsync(database, family, 0, 50, "manifest_fail_key", "v1");
            await database.FlushAsync(family);
            await WriteBatchAsync(database, family, 50, 50, "manifest_fail_key", "v2");
            await database.FlushAsync(family);
            await database.CompactAllAsync();
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var reopenedFamily = await reopened.CreateColumnFamilyAsync("test");
        await AssertBatchAsync(
            reopened,
            reopenedFamily,
            0,
            100,
            "manifest_fail_key",
            static index => index < 50 ? "v1" : "v2");
    }

    [Fact]
    public async Task ShouldPreserveFlushedValuesWhenReopeningAfterBackgroundUploadDelay()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);

        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = await database.CreateColumnFamilyAsync("test");
            await WriteBatchAsync(database, family, 0, 75, "retry_key", "retry_value");
            await database.FlushAsync(family);
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        var reopenedFamily = await reopened.CreateColumnFamilyAsync("test");
        await AssertBatchAsync(
            reopened,
            reopenedFamily,
            0,
            75,
            "retry_key",
            static _ => "retry_value");
    }

    [Fact]
    public async Task ShouldPreserveSnapshotVisibilityWhenFlushingWithSnapshotOpen()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(CreateOptions(directory.Path));
        var family = await database.CreateColumnFamilyAsync("test");
        await WriteBatchAsync(database, family, 0, 100, "exposure_key", "safe_value");
        await using var snapshot = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);

        await database.FlushAsync(family);

        for (var index = 0; index < 100; index++)
        {
            var key = TestBytes.FromString(CreateKey("exposure_key", index));
            var value = Assert.IsType<ReadOnlyMemory<byte>>(await snapshot.GetAsync(key));
            Assert.Equal("safe_value", TestBytes.ToText(value));
        }
    }

    [Fact]
    public async Task ShouldPreserveBothGenerationsWhenReopeningAfterCompactionRequest()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);

        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = await database.CreateColumnFamilyAsync("test");
            await WriteBatchAsync(database, family, 0, 100, "midcompact_key", "gen1");
            await database.FlushAsync(family);
            await WriteBatchAsync(database, family, 100, 100, "midcompact_key", "gen2");
            await database.FlushAsync(family);
            await database.CompactAllAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var reopenedFamily = await reopened.CreateColumnFamilyAsync("test");
        await AssertBatchAsync(
            reopened,
            reopenedFamily,
            0,
            200,
            "midcompact_key",
            static index => index < 100 ? "gen1" : "gen2");
    }

    [Fact]
    public async Task ShouldPreserveValuesWhenReopeningAfterFlushAttempt()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);

        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = await database.CreateColumnFamilyAsync("test");
            await WriteBatchAsync(database, family, 0, 50, "cloud_offline_key", "offline_value");
            await database.FlushAsync(family);
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var reopenedFamily = await reopened.CreateColumnFamilyAsync("test");
        await AssertBatchAsync(
            reopened,
            reopenedFamily,
            0,
            50,
            "cloud_offline_key",
            static _ => "offline_value");
    }

    [Fact]
    public async Task ShouldPreserveMultipleFlushedBatchesWhenReopeningAfterShortUploadWindow()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);

        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = await database.CreateColumnFamilyAsync("test");
            await WriteBatchAsync(database, family, 0, 50, "resume_key", "batch1");
            await database.FlushAsync(family);
            await WriteBatchAsync(database, family, 50, 50, "resume_key", "batch2");
            await database.FlushAsync(family);
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var reopenedFamily = await reopened.CreateColumnFamilyAsync("test");
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await AssertBatchAsync(
            reopened,
            reopenedFamily,
            0,
            100,
            "resume_key",
            static index => index < 50 ? "batch1" : "batch2");
    }

    [Fact]
    public async Task ShouldPreserveFlushedValuesWhenReopeningAfterShortRetryWindow()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);

        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = await database.CreateColumnFamilyAsync("test");
            await WriteBatchAsync(database, family, 0, 60, "dedup_key", "dedup_value");
            await database.FlushAsync(family);
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var reopenedFamily = await reopened.CreateColumnFamilyAsync("test");
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await AssertBatchAsync(
            reopened,
            reopenedFamily,
            0,
            60,
            "dedup_key",
            static _ => "dedup_value");
    }

    static async ValueTask WriteBatchAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        int start,
        int count,
        string keyPrefix,
        string value)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        for (var index = start; index < start + count; index++)
        {
            transaction.Put(
                TestBytes.FromString(CreateKey(keyPrefix, index)),
                TestBytes.FromString(value));
        }

        await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
    }

    static async ValueTask AssertBatchAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        int start,
        int count,
        string keyPrefix,
        Func<int, string> expectedValue)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);
        for (var index = start; index < start + count; index++)
        {
            var key = TestBytes.FromString(CreateKey(keyPrefix, index));
            var value = Assert.IsType<ReadOnlyMemory<byte>>(await transaction.GetAsync(key));
            Assert.Equal(expectedValue(index), TestBytes.ToText(value));
        }
    }

    static string CreateKey(string prefix, int index) => $"{prefix}_{index:0000}";

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.SimulatedCloud(path, "pants-tests", "cloud-recovery/")
            .WithBackgroundCompaction(false);
}
