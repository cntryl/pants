using Cntryl.Pants.Runtime;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Observability;

[Collection(RuntimeDiagnosticsTestGroup.Name)]
public sealed class PantsRuntimeMeterTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldEmitRepresentativeSignalsGivenRealEngineActivity()
    {
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "pants.wal.appends",
            "pants.flush.publications",
            "pants.compactions.completed",
            "pants.reads",
            "pants.recovery.wal_records",
            "pants.transactions.started",
            "pants.transactions.committed",
            "pants.transactions.rolledback"
        };
        using var measurements = Listen(names);
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2, BackgroundEnabled: false));
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await CommitAsync(database, "first", PantsWriteOptions.Sync);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.True((await reopened.Diagnostics.GetRuntimeMetricsAsync()).WalRecoveryRecordsReplayed > 0);
        await reopened.Maintenance.FlushAsync(reopened.ColumnFamilies.DefaultFamily);
        await CommitAsync(reopened, "second", PantsWriteOptions.Buffered);
        await reopened.Maintenance.FlushAsync(reopened.ColumnFamilies.DefaultFamily);
        await using (var read = await reopened.Transactions.BeginAsync(
                         reopened.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadOnly))
        {
            Assert.NotNull(await read.GetAsync("first"u8.ToArray()));
        }

        await reopened.Maintenance.CompactAllAsync();

        await measurements.WaitForAsync(names, AssertionTimeout);
        Assert.All(names, name => Assert.True(measurements[name] > 0, name));
        Assert.False(measurements.HasTags);
    }

    [Fact]
    public async Task ShouldEmitCloudSignalWhileKeepingSnapshotsPerEngine()
    {
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "pants.cloud.wal_uploads.completed"
        };
        using var measurements = Listen(names);
        using var firstDirectory = new TemporaryDirectory();
        using var secondDirectory = new TemporaryDirectory();
        var localOptions = PantsOpenOptions.Local(firstDirectory.Path)
            .WithBackgroundCompaction(false);
        await using var first = await PantsDatabase.OpenAsync(localOptions);
        await using var second = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(secondDirectory.Path).WithBackgroundCompaction(false));
        await CommitAsync(first, "first", PantsWriteOptions.Sync);
        await CommitAsync(second, "second-a", PantsWriteOptions.Sync);
        await CommitAsync(second, "second-b", PantsWriteOptions.Sync);

        var firstMetrics = await first.Diagnostics.GetRuntimeMetricsAsync();
        var secondMetrics = await second.Diagnostics.GetRuntimeMetricsAsync();

        Assert.Equal(1, firstMetrics.WalAppendCount);
        Assert.Equal(2, secondMetrics.WalAppendCount);

        using var cloudDirectory = new TemporaryDirectory();
        var cloudOptions = PantsOpenOptions
            .SimulatedCloud(cloudDirectory.Path, "pants-tests", "runtime-meter/")
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                long.MaxValue,
                long.MaxValue,
                TimeSpan.FromHours(1),
                1))
            .WithBackgroundCompaction(false);
        await using var cloud = await PantsDatabase.OpenAsync(cloudOptions);
        await CommitAsync(cloud, "cloud", PantsWriteOptions.CloudAsync);

        await measurements.WaitForAsync(names, AssertionTimeout);
        Assert.True(measurements["pants.cloud.wal_uploads.completed"] > 0);
        Assert.False(measurements.HasTags);
        Assert.Equal(1, (await first.Diagnostics.GetRuntimeMetricsAsync()).WalAppendCount);
        Assert.Equal(2, (await second.Diagnostics.GetRuntimeMetricsAsync()).WalAppendCount);
    }

    [Fact]
    public async Task ShouldEmitFailureAndRetrySignalsGivenRealEngineFailures()
    {
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "pants.compactions.failed",
            "pants.cloud.flush_retries"
        };
        using var measurements = Listen(names);
        using var localDirectory = new TemporaryDirectory();
        var compactionFailure = new CloudCompactionFailpointHandler();
        var localOptions = PantsOpenOptions.Local(localDirectory.Path)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2, BackgroundEnabled: false));
        await using (var local = await PantsDatabase.OpenForTestingAsync(
                         localOptions,
                         new RuntimeDependencies(compactionFailure)))
        {
            await CommitAsync(local, "first", PantsWriteOptions.Buffered);
            await local.Maintenance.FlushAsync(local.ColumnFamilies.DefaultFamily);
            await CommitAsync(local, "second", PantsWriteOptions.Buffered);
            await local.Maintenance.FlushAsync(local.ColumnFamilies.DefaultFamily);
            compactionFailure.Arm(Failpoint.BeforeCompactionManifestPublish);

            await Assert.ThrowsAsync<PantsIOException>(() => local.Maintenance.CompactAllAsync().AsTask());

            Assert.Equal(1, (await local.Diagnostics.GetRuntimeMetricsAsync()).CompactionFailures);
        }

        using var cloudDirectory = new TemporaryDirectory();
        var cloudFailure = new CloudCompactionFailpointHandler();
        var cloudOptions = PantsOpenOptions
            .SimulatedCloud(cloudDirectory.Path, "pants-tests", "runtime-meter-retry/")
            .WithBackgroundCompaction(false);
        await using var cloud = await PantsDatabase.OpenForTestingAsync(
            cloudOptions,
            new RuntimeDependencies(cloudFailure));
        await CommitAsync(cloud, "retry", PantsWriteOptions.CloudAsync);
        cloudFailure.Arm(Failpoint.BeforeCloudUpload);

        await Assert.ThrowsAsync<PantsIOException>(() =>
            cloud.Maintenance.FlushAsync(cloud.ColumnFamilies.DefaultFamily).AsTask());

        await measurements.WaitForAsync(names, AssertionTimeout);
        Assert.True(measurements["pants.compactions.failed"] > 0);
        Assert.True(measurements["pants.cloud.flush_retries"] > 0);
        Assert.False(measurements.HasTags);
        Assert.True((await cloud.Diagnostics.GetRuntimeMetricsAsync()).FlushRetriesTotal > 0);
    }

    static RuntimeMeterMeasurements Listen(IReadOnlySet<string> names) => new(names);

    static async ValueTask CommitAsync(
        IPantsDatabase database,
        string key,
        PantsWriteOptions writeOptions)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), "value"u8.ToArray());
        await transaction.CommitAsync(writeOptions);
    }
}
