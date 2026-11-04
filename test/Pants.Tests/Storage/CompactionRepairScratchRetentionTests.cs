using Cntryl.Pants.Storage.Internal.Compaction;
using Cntryl.Pants.Storage.Internal.Compaction.Compaction;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class CompactionRepairScratchRetentionTests
{
    [Fact]
    public void ShouldKeepStagingReservationChargedUntilRetainedRepairScratchIsRemoved()
    {
        using var directory = new TemporaryDirectory();
        var stagingDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, ".flush-staging"));
        var retainedPath = Path.Combine(stagingDirectory.FullName, "repair.retained.tmp");
        File.WriteAllBytes(retainedPath, new byte[128]);
        var ledger = new StorageBudgetLedger(new HybridStorageBudgetPolicy(1024), static () => 0);
        var reservation = ledger.Reserve(StorageAdmissionKind.Compaction, 512);
        var staging = new CompactionOutputStaging(512, static (_, _) => ValueTask.CompletedTask);
        var lease = new CompactionStagingLease(reservation, staging,
            () => CompactionRepairScratch.IsCleanupVerified(directory.Path));

        lease.Dispose();

        Assert.Equal(512, ledger.ReservedBytes);
        Assert.False(ledger.TryReserve(StorageAdmissionKind.Compaction, 513, out _));
        Assert.True(File.Exists(retainedPath));

        File.Delete(retainedPath);
        lease.Dispose();

        Assert.Equal(0, ledger.ReservedBytes);
        using var next = ledger.Reserve(StorageAdmissionKind.Compaction, 1024);
        Assert.Equal(1024, ledger.ReservedBytes);
    }

    [Fact]
    public void ShouldRefuseNewRepairGivenRetainedScratchFromFailedCleanup()
    {
        using var directory = new TemporaryDirectory();
        var staging = Directory.CreateDirectory(Path.Combine(directory.Path, ".flush-staging"));
        var retainedPath = Path.Combine(staging.FullName, "repair.retained.tmp");
        var retainedBytes = "retained non-authoritative scratch"u8.ToArray();
        File.WriteAllBytes(retainedPath, retainedBytes);

        var exception = Assert.Throws<PantsResourceLimitException>(() =>
        {
            using var scratch = new CompactionRepairScratch(directory.Path, 1024, PantsPerformanceGoal.Latency);
        });

        Assert.Contains("cleanup", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(retainedBytes, File.ReadAllBytes(retainedPath));
        Assert.Equal(retainedPath, Assert.Single(Directory.EnumerateFiles(staging.FullName)));
    }

    [Fact]
    public void ShouldAllowRepairGivenOnlyUnrelatedFlushStagingFiles()
    {
        using var directory = new TemporaryDirectory();
        var staging = Directory.CreateDirectory(Path.Combine(directory.Path, ".flush-staging"));
        var flushPath = Path.Combine(staging.FullName, "writer.flush.sst.tmp");
        var flushBytes = "unrelated staged flush"u8.ToArray();
        File.WriteAllBytes(flushPath, flushBytes);

        using (var scratch = new CompactionRepairScratch(directory.Path, 1024, PantsPerformanceGoal.Latency))
        {
            Assert.Equal(0, scratch.UsedBytes);
        }

        Assert.Equal(flushBytes, File.ReadAllBytes(flushPath));
        Assert.Equal(flushPath, Assert.Single(Directory.EnumerateFiles(staging.FullName)));
    }
}
