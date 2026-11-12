namespace Cntryl.Pants.Storage.Internal.Compaction;

/// <summary>A compaction's local staging window and the disk reservation that backs it.</summary>
sealed class CompactionStagingLease(
    IDisposable reservation,
    CompactionOutputStaging staging,
    Func<bool>? cleanupVerified = null) : IDisposable
{
    public CompactionOutputStaging Staging { get; } = staging;

    public void Dispose()
    {
        // Keep the window charged until cleanup is proven. After failed scratch deletion this
        // conservative claim lasts until cleanup succeeds or reopening rebuilds disk accounting.
        if (cleanupVerified?.Invoke() is not false)
        {
            reservation.Dispose();
        }
    }
}
