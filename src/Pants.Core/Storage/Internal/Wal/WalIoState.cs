namespace Cntryl.Pants.Storage.Internal.Wal;

/// <summary>
///     Tracks whether the local WAL writer may still accept durable work.
/// </summary>
/// <remarks>
///     <para>
///         The writer is <c>Open</c> until an operation starts its first I/O step that could leave the
///         file in an unknown state - a physical append or an fsync. From then until that operation
///         completes it is <c>Transitioning</c>. A failure while transitioning makes it <c>Fenced</c>:
///         the bytes on disk are no longer known, and after a failed fsync the kernel may drop dirty
///         pages and report the next fsync as successful, so appending more could acknowledge a commit
///         that sits behind a hole. Only restart recovery clears the fence.
///     </para>
///     <para>
///         A failure while still <c>Open</c> is proven to precede any I/O and rejects only the
///         operation that raised it. Mirrors Midge's <c>WalIoState</c>.
///     </para>
/// </remarks>
sealed class WalIoState
{
    Exception? _fenceCause;
    bool _transitioning;

    public bool IsFenced => Volatile.Read(ref _fenceCause) is not null;

    /// <summary>
    ///     Marks the start of an I/O step whose failure would leave the WAL in an unknown state.
    /// </summary>
    public void BeginTransition() => _transitioning = true;

    /// <summary>Marks the current operation's I/O as fully complete.</summary>
    public void CompleteTransition() => _transitioning = false;

    /// <summary>
    ///     Classifies a failed operation, fencing the writer when the failure followed its first
    ///     ambiguous I/O step.
    /// </summary>
    /// <returns><see langword="true" /> when the writer is now fenced.</returns>
    public bool FenceIfTransitioning(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (!_transitioning)
        {
            return IsFenced;
        }

        _transitioning = false;
        Fence(failure);
        return true;
    }

    /// <summary>Fences the writer unconditionally; the first cause is kept.</summary>
    public void Fence(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        _transitioning = false;
        Interlocked.CompareExchange(ref _fenceCause, cause, null);
    }

    public void ThrowIfFenced()
    {
        if (Volatile.Read(ref _fenceCause) is { } cause)
        {
            throw new PantsFencedException(
                "The WAL writer is fenced after an ambiguous write or sync failure; " +
                "reopen the database to recover.",
                cause);
        }
    }
}
