namespace Cntryl.Pants.Cloud.Internal.Leases;

sealed record CloudLeaseRecord(
    string HolderId,
    ulong Epoch,
    string OwnerToken,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc);
