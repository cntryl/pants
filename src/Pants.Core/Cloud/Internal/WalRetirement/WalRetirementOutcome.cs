namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

/// <summary>How a cloud WAL retirement turn ended. None of these is an error.</summary>
enum WalRetirementOutcome
{
    /// <summary>Nothing more can retire until the published manifest advances.</summary>
    Settled,

    /// <summary>The turn used its work quantum with proof work remaining; run again soon.</summary>
    Yielded,

    /// <summary>
    ///     A provider timeout, busy response or exhausted maintenance memory ended the turn. Authority
    ///     is retained and proof progress kept; run again after a backoff.
    /// </summary>
    Deferred
}
