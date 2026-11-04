using Cntryl.Pants.Cloud.Internal.Objects;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class CurrentMidgeCloudWriterTests
{
    [Fact]
    public async Task ShouldExtendCurrentMidgeCloudOutputReleaseItsLeaseAndRecoverFromSharedObjectsOnly()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compatibility", "Midge",
            "c684310d9d8ce8ecd1151c47151d4bea98b348cf", "cloud-sst-midge0");
        foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(directory.Path, Path.GetRelativePath(fixture, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        var objects = new SimulatedCloudObjectStore(Path.Combine(directory.Path, "cloud_store"));
        var leases = new CloudObjectLeaseStore(objects, PantsCloudObjectLayout.LeaseObjectKey);
        var predecessor = Assert.IsType<CloudLeaseSnapshot>(await leases.ReadAsync(CancellationToken.None));
        var clock = new ManualClock(predecessor.Lease.ExpiresAtUtc.AddMinutes(2));
        var options = PantsOpenOptions.SimulatedCloud(directory.Path, "interop", "format9/")
            .WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenForTestingAsync(options,
                         new RuntimeDependencies(leaseClock: clock)))
        {
            await using (var reader = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly))
            {
                Assert.Equal("v0"u8.ToArray(), (await reader.GetAsync("point"u8.ToArray()))?.ToArray());
                Assert.Equal(new byte[] { 0, 255, 0 }, (await reader.GetAsync(Array.Empty<byte>()))?.ToArray());
                Assert.Equal("old"u8.ToArray(), (await reader.GetAsync("delete"u8.ToArray()))?.ToArray());
            }

            await using (var writer = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
            {
                writer.Put("point"u8.ToArray(), "v1"u8.ToArray());
                writer.Put(Array.Empty<byte>(), new byte[] { 0, 255, 1 });
                writer.Delete("delete"u8.ToArray());
                writer.DeleteRange("range/a"u8.ToArray(), "range/z"u8.ToArray());
                await writer.CommitAsync(PantsWriteOptions.CloudStrict);
            }

            var side = await database.ColumnFamilies.GetAsync("side");
            Assert.NotNull(side);
            await using var sideWriter = await database.Transactions.BeginAsync(side, PantsTransactionMode.ReadWrite);
            Assert.Equal("side0"u8.ToArray(), (await sideWriter.GetAsync("cf"u8.ToArray()))?.ToArray());
            sideWriter.Put("cf"u8.ToArray(), "side1"u8.ToArray());
            await sideWriter.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        using (var independent = new CloudLeaseCoordinator(leases, clock, "independent-consumer",
                   TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15)))
        {
            Assert.True(await independent.AcquireAsync(CancellationToken.None) > predecessor.Lease.Epoch);
            await independent.ReleaseAsync(CancellationToken.None);
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory.Path))
        {
            if (Path.GetFileName(entry) == "cloud_store")
            {
                continue;
            }
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, true);
            }
            else
            {
                File.Delete(entry);
            }
        }

        clock.UtcNow += TimeSpan.FromMinutes(2);
        await using var reopened = await PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(leaseClock: clock));
        await using var recovered = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("v1"u8.ToArray(), (await recovered.GetAsync("point"u8.ToArray()))?.ToArray());
        Assert.Equal(new byte[] { 0, 255, 1 }, (await recovered.GetAsync(Array.Empty<byte>()))?.ToArray());
        Assert.Null(await recovered.GetAsync("delete"u8.ToArray()));
        Assert.Null(await recovered.GetAsync("range/a"u8.ToArray()));
        Assert.Equal("end"u8.ToArray(), (await recovered.GetAsync("range/z"u8.ToArray()))?.ToArray());
        var recoveredSide = await reopened.ColumnFamilies.GetAsync("side");
        Assert.NotNull(recoveredSide);
        await using var sideReader = await reopened.Transactions.BeginAsync(recoveredSide, PantsTransactionMode.ReadOnly);
        Assert.Equal("side1"u8.ToArray(), (await sideReader.GetAsync("cf"u8.ToArray()))?.ToArray());
    }
}
