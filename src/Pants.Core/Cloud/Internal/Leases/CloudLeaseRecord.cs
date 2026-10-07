namespace Cntryl.Pants.Cloud.Internal.Leases;

/// <param name="ExpiryMalformed">
///     The stored expiry could not be parsed. Takeover is then indeterminate (the holder may still
///     be live), but the current owner's renewal writes a fresh expiry and repairs it.
/// </param>
sealed record CloudLeaseRecord(
    string HolderId,
    ulong Epoch,
    string OwnerToken,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool ExpiryMalformed = false);
