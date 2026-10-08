namespace Cntryl.Pants.Storage.Internal.Wal;

/// <summary>
///     Bounds the Buffered durability window: buffered appends are fsynced by a background turn
///     once either a maximum delay passes or enough bytes accumulate, with no further caller
///     activity needed. The commit that triggered it has already returned, so no caller waits on
///     the fsync.
/// </summary>
sealed class BufferedWalSyncScheduler : IDisposable
{
    public static readonly TimeSpan DefaultMaximumDelay = TimeSpan.FromMilliseconds(100);
    public const long DefaultMaximumBytes = 64 * 1024;

    readonly long _maximumBytes;
    readonly TimeSpan _maximumDelay;
    readonly Timer _timer;
    int _armed;

    public BufferedWalSyncScheduler(TimeSpan maximumDelay, long maximumBytes, Action sync)
    {
        ArgumentNullException.ThrowIfNull(sync);
        _maximumDelay = maximumDelay;
        _maximumBytes = maximumBytes;
        _timer = new Timer(
            _ =>
            {
                Volatile.Write(ref _armed, 0);
                sync();
            },
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>Call after an append that left <paramref name="unsyncedBytes" /> unsynced.</summary>
    public void NotifyAppend(long unsyncedBytes)
    {
        try
        {
            if (unsyncedBytes >= _maximumBytes)
            {
                Volatile.Write(ref _armed, 1);
                _ = _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
            else if (Interlocked.CompareExchange(ref _armed, 1, 0) == 0)
            {
                _ = _timer.Change(_maximumDelay, Timeout.InfiniteTimeSpan);
            }
        }
        catch (ObjectDisposedException)
        {
            // The store is closing; shutdown syncs the WAL itself.
        }
    }

    public void Dispose() => _timer.Dispose();
}
