namespace Cntryl.Pants.Storage.Internal.Compaction;

/// <summary>A compaction's local staging window and the disk reservation that backs it.</summary>
sealed class CompactionStagingLease(IDisposable reservation, CompactionOutputStaging staging) : IDisposable
{
    public CompactionOutputStaging Staging { get; } = staging;

    public void Dispose() => reservation.Dispose();
}
