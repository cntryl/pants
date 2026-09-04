using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsHybridStorageTests
{
    const long LocalBudgetBytes = 128 * 1024;

    [Fact]
    public async Task ShouldApplySimulatedCloudLocalStorageBudgetWhenOpeningSimulatedCloud()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path, 8 * 1024 * 1024);
        await PutAsync(database, "resident", CreateValue(128 * 1024, 11));
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();

        Assert.Equal(8 * 1024 * 1024, metrics.HybridMaximumLocalBytes);
        Assert.True(metrics.HybridTotalCommittedBytes > 0);
        Assert.True(metrics.HybridFreeBytes > 0);
        Assert.True(metrics.HybridUsagePercent > 0);
    }

    [Fact]
    public async Task ShouldTriggerEvictionAtHighWatermark()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        var value = CreateValue(256 * 1024, 17);
        await PutAsync(database, "large", value);

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        Assert.Empty(LocalSsts(directory.Path));
        Assert.Single(CloudSsts(directory.Path));
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(metrics.HybridFreeBytes > 0);
        Assert.True(metrics.HybridUsagePercent < 90);
    }

    [Fact]
    public async Task ShouldEvictAtEmergencyWatermarkDespiteAnActiveSnapshot()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        var value = CreateValue(256 * 1024, 23);
        await PutAsync(database, "pressure", value);
        await using var snapshot = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        await using var admitted = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        admitted.Put("admitted"u8.ToArray(), "value"u8.ToArray());

        await admitted.CommitAsync(PantsWriteOptions.CloudStrict);

        Assert.Empty(LocalSsts(directory.Path));
        Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).NoSpaceEvents);
    }

    [Fact]
    public async Task ShouldBlockWritesAtEmergencyWatermark()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new PersistentThrowingFlushFailpointHandler(
            Failpoint.BeforeHybridSstEviction);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path),
            new RuntimeDependencies(failpoints));
        try
        {
            await PutAsync(database, "pressure", CreateValue(256 * 1024, 101));
            await Assert.ThrowsAsync<PantsIOException>(() =>
                database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask());
            await using var blocked = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            blocked.Put("blocked"u8.ToArray(), "value"u8.ToArray());

            await Assert.ThrowsAsync<PantsNoSpaceException>(() =>
                blocked.CommitAsync(PantsWriteOptions.CloudStrict).AsTask());

            Assert.Single(LocalSsts(directory.Path));
            Assert.Single(CloudSsts(directory.Path));
            Assert.Equal(1, (await database.Diagnostics.GetRuntimeMetricsAsync()).NoSpaceEvents);
        }
        finally
        {
            failpoints.Release();
        }
    }

    [Fact]
    public async Task ShouldResumeWritesGivenCloudUploadCompletesWhenEmergencyWatermarkIsActive()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new PersistentThrowingFlushFailpointHandler(
            Failpoint.BeforeHybridSstEviction);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path),
            new RuntimeDependencies(failpoints));
        await PutAsync(database, "pressure", CreateValue(256 * 1024, 103));
        await Assert.ThrowsAsync<PantsIOException>(() =>
            database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask());
        await using (var blocked = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            blocked.Put("blocked"u8.ToArray(), "value"u8.ToArray());
            await Assert.ThrowsAsync<PantsNoSpaceException>(() =>
                blocked.CommitAsync(PantsWriteOptions.CloudStrict).AsTask());
        }

        failpoints.Release();
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        Assert.Empty(LocalSsts(directory.Path));
        for (var index = 0; index < 5; index++)
        {
            await PutAsync(database, $"resumed-{index}", "value"u8.ToArray());
        }
    }

    [Fact]
    public async Task ShouldContinueWritesAfterSnapshotVisibleCacheEviction()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await PutAsync(database, "pressure", CreateValue(256 * 1024, 29));
        var snapshot = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        Assert.Empty(LocalSsts(directory.Path));
        await using (var admitted = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            admitted.Put("admitted"u8.ToArray(), "value"u8.ToArray());
            await admitted.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        Assert.Equal(
            256 * 1024,
            Assert.IsType<ReadOnlyMemory<byte>>(
                await snapshot.GetAsync("pressure"u8.ToArray())).Length);
        await snapshot.DisposeAsync();
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        for (var index = 0; index < 5; index++)
        {
            await PutAsync(database, $"resumed-{index}", "value"u8.ToArray());
        }

        Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).NoSpaceEvents);
    }

    [Fact]
    public async Task ShouldPreferLocalReadsBeforeEviction()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path, 8 * 1024 * 1024);
        await PutAsync(database, "cached", "value"u8.ToArray());
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var cloudSst = Assert.Single(CloudSsts(directory.Path));
        File.Delete(cloudSst);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        var value = await reader.GetAsync("cached"u8.ToArray());

        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(value)));
        Assert.False(File.Exists(cloudSst));
    }

    [Fact]
    public async Task ShouldFetchFromCloudAfterLocalEviction()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await PutAsync(database, "evicted", CreateValue(256 * 1024, 31));
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        Assert.Empty(LocalSsts(directory.Path));
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        var value = await reader.GetAsync("evicted"u8.ToArray());

        Assert.Equal(256 * 1024, Assert.IsType<ReadOnlyMemory<byte>>(value).Length);
        Assert.Empty(LocalSsts(directory.Path));
    }

    [Fact]
    public async Task ShouldPersistEvictionStateAcrossRestart()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            await PutAsync(database, "persisted", CreateValue(256 * 1024, 37));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            Assert.Empty(LocalSsts(directory.Path));
        }

        await using var reopened = await OpenAsync(directory.Path);

        Assert.Empty(LocalSsts(directory.Path));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await reader.GetAsync("persisted"u8.ToArray());
        Assert.Equal(256 * 1024, Assert.IsType<ReadOnlyMemory<byte>>(value).Length);
    }

    [Fact]
    public async Task ShouldHandleCloudUnavailableDuringEviction()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new HybridEvictionFailpointHandler(
            Failpoint.BeforeCloudUpload);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path),
            new RuntimeDependencies(failpoints));
        await PutAsync(database, "retained", CreateValue(256 * 1024, 41));

        await Assert.ThrowsAsync<PantsIOException>(() =>
            database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask());

        Assert.Single(LocalSsts(directory.Path));
        Assert.Empty(CloudSsts(directory.Path));
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await reader.GetAsync("retained"u8.ToArray());
        Assert.Equal(256 * 1024, Assert.IsType<ReadOnlyMemory<byte>>(value).Length);
    }

    [Fact]
    public async Task ShouldPublishSstBeforeEvictingLocalCacheFile()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new HybridEvictionFailpointHandler(Failpoint.AfterCloudUpload);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path),
            new RuntimeDependencies(failpoints));
        await PutAsync(database, "ordered", CreateValue(256 * 1024, 47));

        await Assert.ThrowsAsync<PantsIOException>(() =>
            database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask());

        Assert.Single(CloudSsts(directory.Path));
        Assert.Single(LocalSsts(directory.Path));
    }

    [Fact]
    public async Task ShouldEvictLocalSstGivenAPreFlushSnapshotCanReadItsMemtableRoot()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await PutAsync(database, "pinned", CreateValue(256 * 1024, 43));
        await using var snapshot = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        Assert.Empty(LocalSsts(directory.Path));
        var value = await snapshot.GetAsync("pinned"u8.ToArray());
        Assert.Equal(256 * 1024, Assert.IsType<ReadOnlyMemory<byte>>(value).Length);
    }

    [Fact]
    public async Task ShouldEvictSnapshotVisibleSstAndReadItFromRemoteAuthority()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path, 300 * 1024);
        await PutAsync(database, "pinned", CreateValue(64 * 1024, 53));
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var pinnedSst = Assert.Single(LocalSsts(directory.Path));
        await using var snapshot = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await PutAsync(database, "newer", CreateValue(256 * 1024, 59));

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        Assert.DoesNotContain(pinnedSst, LocalSsts(directory.Path));
        Assert.Single(LocalSsts(directory.Path));
        Assert.Equal(
            64 * 1024,
            Assert.IsType<ReadOnlyMemory<byte>>(
                await snapshot.GetAsync("pinned"u8.ToArray())).Length);
        Assert.Null(await snapshot.GetAsync("newer"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldKeepLocalSstGivenMirrorCycleReportsPersistenceAnomaly()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        var cloudSstDirectory = Path.Combine(directory.Path, "cloud_store", "sst");
        Directory.CreateDirectory(cloudSstDirectory);
        File.WriteAllBytes(Path.Combine(cloudSstDirectory, "malformed.txt"), [1]);
        await PutAsync(database, "retained", CreateValue(256 * 1024, 61));

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        Assert.Single(LocalSsts(directory.Path));
        Assert.Equal(
            PantsEngineHealth.Degraded,
            (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldKeepLocalSstGivenCloudCopyDisappearsBeforeEviction()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new DeleteCloudSstBeforeEvictionFailpointHandler(directory.Path);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path),
            new RuntimeDependencies(failpoints));
        await PutAsync(database, "retained", CreateValue(256 * 1024, 67));

        await Assert.ThrowsAsync<PantsRecoveryFailedException>(() =>
            database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask());

        Assert.Single(LocalSsts(directory.Path));
        Assert.Empty(CloudSsts(directory.Path));
    }

    [Fact]
    public async Task ShouldKeepSnapshotEvictedCacheEmptyAfterCloudAsyncMaintenance()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path, 280 * 1024);
        await PutAsync(database, "retained", CreateValue(256 * 1024, 73));
        var snapshot = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        Assert.Empty(LocalSsts(directory.Path));
        await snapshot.DisposeAsync();
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("maintenance"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (LocalSsts(directory.Path).Length != 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }

        Assert.Empty(LocalSsts(directory.Path));
    }

    [Fact]
    public async Task ShouldReportOnlyPlannedEvictionsWhileEvictionIsBlocked()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new BlockingHybridEvictionFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path, 160 * 1024),
            new RuntimeDependencies(failpoints));
        for (var index = 0; index < 4; index++)
        {
            await PutAsync(database, $"seed-{index}", CreateValue(16 * 1024, 79 + index));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        failpoints.Arm();
        await PutAsync(database, "cross-high", CreateValue(128 * 1024, 89));
        var flush = database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask();
        try
        {
            await failpoints.WaitUntilBlockedAsync(TimeSpan.FromSeconds(5));
            var localSstCount = LocalSsts(directory.Path).Length;
            var pending = (await database.Diagnostics.GetRuntimeMetricsAsync()).HybridPendingEvictions;

            Assert.InRange(pending, 1, localSstCount - 1);
        }
        finally
        {
            failpoints.Release();
        }

        await flush;
        Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).HybridPendingEvictions);
    }

    [Fact]
    public async Task ShouldKeepLocalUsageBoundedAcrossBurstyFlushCycles()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path, 192 * 1024);
        var sawPartialEviction = false;
        for (var index = 0; index < 20; index++)
        {
            await PutAsync(database, $"burst-{index}", CreateValue(32 * 1024, 97 + index));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            var localCount = LocalSsts(directory.Path).Length;
            var cloudCount = CloudSsts(directory.Path).Length;
            var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
            sawPartialEviction |= localCount > 0 && localCount < cloudCount;
            Assert.True(metrics.HybridUsagePercent < 130);
            Assert.Equal(0, metrics.HybridPendingEvictions);
        }

        Assert.True(sawPartialEviction);
    }

    static PantsOpenOptions CreateOptions(string path, long budgetBytes = LocalBudgetBytes) =>
        PantsOpenOptions.SimulatedCloud(path, "pants-tests", "hybrid/")
            .WithSimulatedCloudLocalStorageBudget(budgetBytes)
            .WithBackgroundCompaction(false);

    static ValueTask<IPantsDatabase> OpenAsync(
        string path,
        long budgetBytes = LocalBudgetBytes) =>
        PantsDatabase.OpenAsync(CreateOptions(path, budgetBytes));

    static async ValueTask PutAsync(
        IPantsDatabase database,
        string key,
        ReadOnlyMemory<byte> value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), value);
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }

    static byte[] CreateValue(int length, int seed)
    {
        var value = new byte[length];
        new Random(seed).NextBytes(value);
        return value;
    }

    static string[] LocalSsts(string root) =>
        Directory.GetFiles(Path.Combine(root, "sst"), "*.sst");

    static string[] CloudSsts(string root) =>
        Directory.GetFiles(Path.Combine(root, "cloud_store", "sst"), "*.sst");
}
