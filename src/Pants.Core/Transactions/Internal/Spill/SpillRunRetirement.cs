namespace Cntryl.Pants.Transactions.Internal.Spill;

/// <summary>
///     Removes spill-run files once their transaction has finished, and keeps the local-disk budget
///     charge for any file that could not be removed until a later attempt succeeds.
/// </summary>
/// <remarks>
///     A charge is released only when the bytes it stands for are gone from disk. Releasing it while a
///     failed file still exists would let later spills be admitted past the budget. Retained entries
///     are retried at the start of each spill write, before that write reserves budget, so a
///     successful removal frees the charge in time for the write that needs it.
/// </remarks>
sealed class SpillRunRetirement
{
    readonly Lock _gate = new();
    readonly ISpillFileRemover _remover;
    readonly List<PendingRetirement> _pending = [];

    public SpillRunRetirement(ISpillFileRemover remover)
    {
        _remover = remover ?? throw new ArgumentNullException(nameof(remover));
    }

    /// <summary>
    ///     Takes ownership of the files and their charges. Charges are released immediately when every
    ///     file is removed; otherwise they are held until a later <see cref="RetryPending" /> clears
    ///     the remaining files.
    /// </summary>
    public void Retire(
        IReadOnlyList<string> paths,
        IReadOnlyList<StorageBudgetLedger.StorageReservation> reservations)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(reservations);
        var remaining = RemoveAll(paths);
        if (remaining.Count == 0)
        {
            ReleaseAll(reservations);
            return;
        }

        lock (_gate)
        {
            _pending.Add(new PendingRetirement(remaining, reservations.ToArray()));
        }
    }

    /// <summary>
    ///     Attempts the removals still outstanding and releases the charges of every entry that is now
    ///     fully gone.
    /// </summary>
    public void RetryPending()
    {
        PendingRetirement[] snapshot;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            snapshot = _pending.ToArray();
        }

        foreach (var entry in snapshot)
        {
            var remaining = RemoveAll(entry.Paths);
            if (remaining.Count != 0)
            {
                lock (_gate)
                {
                    entry.Paths = remaining;
                }

                continue;
            }

            var removed = false;
            lock (_gate)
            {
                removed = _pending.Remove(entry);
            }

            if (removed)
            {
                ReleaseAll(entry.Reservations);
            }
        }
    }

    List<string> RemoveAll(IReadOnlyList<string> paths)
    {
        var remaining = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                _remover.Remove(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                PantsDiagnostics.TransactionSpillDeleteFailures.Add(1);
                remaining.Add(path);
            }
        }

        return remaining;
    }

    static void ReleaseAll(IReadOnlyList<StorageBudgetLedger.StorageReservation> reservations)
    {
        foreach (var reservation in reservations)
        {
            reservation.Dispose();
        }
    }

    sealed class PendingRetirement(
        List<string> paths,
        StorageBudgetLedger.StorageReservation[] reservations)
    {
        public List<string> Paths { get; set; } = paths;

        public StorageBudgetLedger.StorageReservation[] Reservations { get; } = reservations;
    }
}
