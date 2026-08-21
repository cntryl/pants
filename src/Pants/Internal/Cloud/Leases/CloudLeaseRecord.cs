namespace Pants;

internal sealed record CloudLeaseRecord(
    string HolderId,
    ulong Epoch,
    string OwnerToken,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc);
