using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class LocalCommittedBytesTests
{
    /// <summary>
    ///     The figure the local-storage budget admits against. It is cached to keep a per-commit
    ///     call off a stat-per-file path, so it has to keep agreeing with the filesystem across
    ///     publication, eviction and obsolete-file collection — a cache that drifts low silently
    ///     overruns the operator's cap, and one that drifts high refuses writes that should succeed.
    /// </summary>
    [Fact]
    public async Task ShouldAgreeWithTheFilesystemAcrossAWorkload()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)))
        {
            var family = database.ColumnFamilies.DefaultFamily;
            for (var generation = 0; generation < 4; generation++)
            {
                for (var index = 0; index < 8; index++)
                {
                    await using var transaction = await database.Transactions.BeginAsync(
                        family,
                        PantsTransactionMode.ReadWrite);
                    transaction.Put(
                        TestBytes.FromString($"key-{generation}-{index}"),
                        new byte[512]);
                    await transaction.CommitAsync(PantsWriteOptions.Sync);
                }

                await database.Maintenance.FlushAsync(family);
            }

            await database.Maintenance.CompactAllAsync();
        }

        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        using var store = LocalDiskStore.Open(directory.Path, state);

        // Read twice: the first populates the cache, the second must not diverge from it.
        var first = store.LocalCommittedBytes;
        var second = store.LocalCommittedBytes;

        Assert.Equal(first, second);
        Assert.Equal(MeasureOnDisk(directory.Path), second);
    }

    [Fact]
    public void ShouldReportZeroForAnEmptyDatabase()
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        using var store = LocalDiskStore.Open(directory.Path, state);

        Assert.Equal(MeasureOnDisk(directory.Path), store.LocalCommittedBytes);
    }

    [Fact]
    public void ShouldChargeRetainedStartupResidueUntilItsFilesAreDeleted()
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        using (LocalDiskStore.Open(directory.Path, state))
        {
        }

        var residuePaths = new[]
        {
            Path.Combine(directory.Path, "cloud_recovery", "nested", "cloud.sst"),
            Path.Combine(directory.Path, "sst", ".flush-staging", "nested", "staging.tmp"),
            Path.Combine(directory.Path, "sst", "orphan.sst.tmp"),
            Path.Combine(directory.Path, "startup-residue.tmp"),
            Path.Combine(directory.Path, "sst", "orphan.sst")
        };
        foreach (var path in residuePaths)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[128 + residuePaths.Length]);
        }

        var failpoints = new PersistentThrowingFlushFailpointHandler(
            Failpoint.BeforeStartupResidueDelete);
        using var hybridCache = new HybridCacheManager(1024 * 1024);
        using var store = LocalDiskStore.Open(
            directory.Path,
            state,
            failpoints: failpoints,
            hybridCache: hybridCache);

        var expectedBytes = residuePaths.Sum(path => new FileInfo(path).Length);
        Assert.All(residuePaths, path => Assert.True(File.Exists(path)));
        Assert.Equal(expectedBytes, store.LocalCommittedBytes);
        Assert.Equal(expectedBytes, hybridCache.Ledger.ChargedBytes);
        var walAdmissionBytes = hybridCache.Ledger.GetAvailableBytes(StorageAdmissionKind.Wal);
        Assert.Throws<PantsNoSpaceException>(() => hybridCache.Ledger.Reserve(
            StorageAdmissionKind.Wal,
            walAdmissionBytes + 1));

        foreach (var path in residuePaths)
        {
            File.Delete(path);
        }

        Assert.Equal(0, store.LocalCommittedBytes);
        Assert.Equal(0, hybridCache.Ledger.ChargedBytes);
        using var reservation = hybridCache.Ledger.Reserve(
            StorageAdmissionKind.Wal,
            hybridCache.Ledger.GetAdmissionCeilingBytes(StorageAdmissionKind.Wal));
    }

    static long MeasureOnDisk(string root)
    {
        var total = 0L;
        foreach (var pattern in new[] { ("sst", "*.sst"), ("wal", "*.wal"), ("wal", "wal.log") })
        {
            var directory = Path.Combine(root, pattern.Item1);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(
                         directory,
                         pattern.Item2,
                         SearchOption.TopDirectoryOnly))
            {
                total += new FileInfo(path).Length;
            }
        }

        return total;
    }
}
