namespace Cntryl.Pants.Storage;

/// <summary>
///     The operator outcome class of a storage verification. Each value's integer is the exit code
///     of the reference engine's <c>midge verify</c> command, so a host that wraps verification in
///     its own tool can return <c>(int)kind</c> as its process exit code. The values are stable.
/// </summary>
public enum PantsStorageVerificationOutcomeKind
{
    /// <summary>Storage verified and is healthy.</summary>
    Healthy = 0,

    /// <summary>Storage verified degraded, in salvage mode or write-stalled, or failed under backpressure.</summary>
    Degraded = 1,

    /// <summary>The verification request itself was invalid.</summary>
    Usage = 2,

    /// <summary>The storage path is missing, inaccessible or invalid, or storage authority was lost.</summary>
    Storage = 3,

    /// <summary>Persisted state is corrupt or incompatible.</summary>
    Corruption = 4,

    /// <summary>An unexpected internal failure or engine defect.</summary>
    Internal = 5
}
