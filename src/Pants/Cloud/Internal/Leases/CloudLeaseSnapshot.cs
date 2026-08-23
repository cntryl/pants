namespace Cntryl.Pants.Cloud.Internal.Leases;

sealed record CloudLeaseSnapshot(
    CloudLeaseRecord Lease,
    string Version);
