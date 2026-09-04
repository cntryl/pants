namespace Cntryl.Pants.Transactions;

public enum PantsConflictPolicy
{
    /// <summary>Applies writes in commit order without rejecting overlapping concurrent writes.</summary>
    LastWriteWins,

    /// <summary>
    /// Rejects writes whose write-set keys or ranges changed after the transaction began.
    /// Ordinary point reads and scans are snapshot-stable but are not implicitly protected at commit;
    /// use <see cref="IPantsTransaction.AssertValue"/> for a read that must remain unchanged.
    /// </summary>
    AbortOnWriteConflict
}
