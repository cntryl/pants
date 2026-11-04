namespace Cntryl.Pants.Storage.Internal.Wal;

/// <summary>
///     An accepted logical WAL unit whose physical encoding failed stops the physical writer.
///     This is separate from ambiguous I/O: the original encoding error is definite, but later
///     work cannot pass the failed unit in the writer's accepted order.
/// </summary>
sealed class WalEncodingState
{
    PantsRecoveryFailedException? _failure;

    public PantsRecoveryFailedException? Failure => Volatile.Read(ref _failure);

    public void Poison(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        Interlocked.CompareExchange(ref _failure, new PantsRecoveryFailedException(
            "The local WAL is poisoned after an accepted transaction batch could not be encoded; " +
            "reopen the database to recover.", cause), null);
    }

    public void ThrowIfPoisoned()
    {
        if (Failure is { } failure)
        {
            throw failure;
        }
    }
}
