namespace Cntryl.Pants;

internal sealed record CloudLeaseSnapshot(
    CloudLeaseRecord Lease,
    string Version);
