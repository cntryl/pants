using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudMirrorEfficiencyTests
{
    [Fact]
    public async Task ShouldNotMirrorCloudStorageGivenTransactionsCompleteWithoutWrites()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new CountingFailpointHandler(Failpoint.BeforeCloudUpload);
        await using var database = await OpenSeededAsync(directory.Path, failpoints);
        var baseline = failpoints.HitCount;

        await using (var committed = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            await committed.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        await using (var rolledBack = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            await rolledBack.RollbackAsync();
        }

        await using (var disposed = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadOnly))
        {
            Assert.NotNull(await disposed.GetAsync("seed"u8.ToArray()));
            await using var scan = await disposed.ScanAsync(new PantsScanQuery());
            await foreach (var entry in scan)
            {
                Assert.Equal("seed"u8.ToArray(), entry.Key.ToArray());
                break;
            }
        }

        Assert.Equal(baseline, failpoints.HitCount);
    }

    [Fact]
    public async Task ShouldNotRepublishImmutableSstGivenCloudMirrorHasNoChanges()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new CountingFailpointHandler(Failpoint.BeforeCloudUpload);
        await using var database = await OpenSeededAsync(directory.Path, failpoints);
        var baseline = failpoints.HitCount;
        var remoteSst = Assert.Single(Directory.GetFiles(
            Path.Combine(directory.Path, "cloud_store", "sst"),
            "*.sst"));
        var remoteManifest = Path.Combine(
            directory.Path,
            "cloud_store",
            "metadata",
            "manifest.snapshot.json");
        var sentinel = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(remoteSst, sentinel);
        File.SetLastWriteTimeUtc(remoteManifest, sentinel);
        var sstWriteTime = File.GetLastWriteTimeUtc(remoteSst);
        var manifestWriteTime = File.GetLastWriteTimeUtc(remoteManifest);

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        Assert.Equal(baseline, failpoints.HitCount);
        Assert.Equal(sstWriteTime, File.GetLastWriteTimeUtc(remoteSst));
        Assert.Equal(manifestWriteTime, File.GetLastWriteTimeUtc(remoteManifest));
    }

    static async ValueTask<IPantsDatabase> OpenSeededAsync(
        string path,
        IFailpointHandler failpoints)
    {
        var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.SimulatedCloud(path, "pants-tests", "mirror-efficiency/")
                .WithBackgroundCompaction(false),
            new RuntimeDependencies(failpoints));
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("seed"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        return database;
    }
}
