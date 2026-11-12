using System.Globalization;
using System.Text.Json.Nodes;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class BackupLeaseAuthorityTests
{
    const string LeasePath = "cloud_store/" + PantsCloudObjectLayout.LeaseObjectKey;
    const string StripePath = "cloud_store/.midge-locks/stripe-00.lock";

    [Fact]
    public async Task ShouldRestoreIndependentSimulatedWriterWhileBackupSourceRemainsLive()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        var artifact = Path.Combine(directory.Path, "backup");
        var restoredPath = Path.Combine(directory.Path, "restored");
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var source = await OpenAsync(sourcePath, clock);
        await SeedAsync(source);
        var manifest = await source.BackupAsync(artifact, TimeSpan.FromSeconds(10));
        var sourceAuthority = ReadAuthority(sourcePath);
        var sourceLease = ReadLeaseFields(sourcePath);
        var capturedLease = ReadLeaseFields(Path.Combine(artifact, "objects"));
        foreach (var field in new[] { "lease_version", "fencing_epoch", "holder", "owner", "acquired" })
        {
            Assert.Equal(sourceLease[field], capturedLease[field]);
        }
        Assert.Equal(DateTimeOffset.UnixEpoch, DateTimeOffset.Parse(capturedLease["expires"], CultureInfo.InvariantCulture));

        await PantsDatabase.RestoreBackupAsync(artifact, Options(restoredPath));
        Assert.Equal(File.ReadAllBytes(Path.Combine(artifact, "objects", "cloud_store", CloudMetadataAuthority.PointerKey)),
            File.ReadAllBytes(Path.Combine(restoredPath, "cloud_store", CloudMetadataAuthority.PointerKey)));
        await using var restored = await OpenAsync(restoredPath, clock);

        Assert.DoesNotContain(manifest.Objects, static item => item.Path.StartsWith("cloud_store/.midge-locks/", StringComparison.Ordinal));
        Assert.Equal(sourceAuthority, ReadAuthority(sourcePath));
        Assert.Equal(PantsEngineHealth.Healthy, (await source.Diagnostics.GetRuntimeMetricsAsync()).Health);
        Assert.Equal(PantsEngineHealth.Healthy, (await restored.Diagnostics.GetRuntimeMetricsAsync()).Health);
        Assert.NotEqual(File.ReadAllBytes(Path.Combine(sourcePath, LeasePath)),
            File.ReadAllBytes(Path.Combine(restoredPath, LeasePath)));
        var restoredLease = ReadLeaseFields(restoredPath);
        Assert.NotEqual(sourceLease["owner"], restoredLease["owner"]);
        Assert.True(ulong.Parse(restoredLease["fencing_epoch"], CultureInfo.InvariantCulture) >
                    ulong.Parse(sourceLease["fencing_epoch"], CultureInfo.InvariantCulture));
        await AssertValueAsync(restored);
        await AssertValueAsync(source);
    }

    [Fact]
    public async Task ShouldRestoreLegacyActiveLeaseCopyWithoutInheritingSourceAuthorityOrNativeStripes()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        var artifact = Path.Combine(directory.Path, "backup");
        var restoredPath = Path.Combine(directory.Path, "restored");
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var source = await OpenAsync(sourcePath, clock);
        await SeedAsync(source);
        await source.BackupAsync(artifact, TimeSpan.FromSeconds(10));
        await PutArtifactObjectAsync(artifact, LeasePath, File.ReadAllBytes(Path.Combine(sourcePath, LeasePath)));
        await PutArtifactObjectAsync(artifact, StripePath, "legacy native lock"u8.ToArray());
        var sourceAuthority = ReadAuthority(sourcePath);

        await PantsDatabase.RestoreBackupAsync(artifact, Options(restoredPath));

        Assert.False(Directory.Exists(Path.Combine(restoredPath, "cloud_store", ".midge-locks")));
        await using var restored = await OpenAsync(restoredPath, clock);
        Assert.Equal(sourceAuthority, ReadAuthority(sourcePath));
        Assert.Equal(PantsEngineHealth.Healthy, (await source.Diagnostics.GetRuntimeMetricsAsync()).Health);
        Assert.Equal(PantsEngineHealth.Healthy, (await restored.Diagnostics.GetRuntimeMetricsAsync()).Health);
        await AssertValueAsync(restored);
    }

    [Theory]
    [InlineData(LeasePath)]
    [InlineData(StripePath)]
    public async Task ShouldRejectCorruptLegacyCoordinatorObjectsBeforePublishingRestore(string relativePath)
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        var artifact = Path.Combine(directory.Path, "backup");
        var restoredPath = Path.Combine(directory.Path, "restored");
        await using var source = await OpenAsync(sourcePath, new ManualClock(DateTimeOffset.UtcNow));
        await SeedAsync(source);
        await source.BackupAsync(artifact, TimeSpan.FromSeconds(10));
        if (relativePath == StripePath)
        {
            await PutArtifactObjectAsync(artifact, StripePath, "legacy native lock"u8.ToArray());
        }
        var path = Path.Combine(artifact, "objects", relativePath);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[0] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);

        await Assert.ThrowsAsync<PantsCorruptionException>(() =>
            PantsDatabase.RestoreBackupAsync(artifact, Options(restoredPath)).AsTask());

        Assert.False(Directory.Exists(restoredPath));
        Assert.Equal(PantsEngineHealth.Healthy, (await source.Diagnostics.GetRuntimeMetricsAsync()).Health);
    }

    static PantsOpenOptions Options(string path) =>
        PantsOpenOptions.SimulatedCloud(path, "bucket", "prefix").WithBackgroundCompaction(false);

    static ValueTask<IPantsDatabase> OpenAsync(string path, ManualClock clock) =>
        PantsDatabase.OpenForTestingAsync(Options(path), new RuntimeDependencies(
            leaseHeartbeatInterval: TimeSpan.FromHours(1), leaseClock: clock));

    static async Task SeedAsync(IPantsDatabase database)
    {
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
        {
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
        }
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }

    static async Task AssertValueAsync(IPantsDatabase database)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("value"u8.ToArray(), (await transaction.GetAsync("key"u8.ToArray()))?.ToArray());
    }

    static (string Path, string Bytes)[] ReadAuthority(string root) =>
        new[] { LeasePath, "cloud_store/" + PantsCloudObjectLayout.WalCatalogObjectKey,
                "cloud_store/" + PantsCloudObjectLayout.WalCatalogMirrorObjectKey,
                "cloud_store/" + CloudMetadataAuthority.PointerKey }
            .Select(path => (path, Convert.ToHexString(File.ReadAllBytes(Path.Combine(root, path)))))
            .ToArray();

    static Dictionary<string, string> ReadLeaseFields(string root) =>
        File.ReadAllLines(Path.Combine(root, LeasePath))
            .Select(static line => line.Split(": ", 2, StringSplitOptions.None))
            .ToDictionary(static fields => fields[0], static fields => fields[1], StringComparer.Ordinal);

    static async Task PutArtifactObjectAsync(string artifact, string relativePath, byte[] bytes)
    {
        var path = Path.Combine(artifact, "objects", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        var manifestPath = Path.Combine(artifact, "backup.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        var objects = manifest["objects"]!.AsArray();
        var existing = objects.SingleOrDefault(item => item!["path"]!.GetValue<string>() == relativePath);
        if (existing is not null)
        {
            objects.Remove(existing);
        }
        objects.Add(new JsonObject
        {
            ["path"] = relativePath,
            ["size_bytes"] = bytes.Length,
            ["crc32_c"] = DiskFormat.Crc32C(bytes)
        });
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
    }
}
