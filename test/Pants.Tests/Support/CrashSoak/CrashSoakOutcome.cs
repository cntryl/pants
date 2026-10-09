namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>What the ledger proves about one operation after its child process was killed.</summary>
enum CrashSoakOutcome
{
    /// <summary>The child never dispatched the operation, so none of its effects may be visible.</summary>
    NotDispatched,

    /// <summary>
    ///     The operation was dispatched but its outcome was never recorded, so it must be visible
    ///     entirely or not at all.
    /// </summary>
    Unknown,

    /// <summary>The engine acknowledged the operation.</summary>
    Acked,

    /// <summary>The engine rejected the operation, or it was rolled back, so none of it may be visible.</summary>
    Failed
}
