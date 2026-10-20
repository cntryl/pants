namespace Cntryl.Pants.Runtime.Internal.Services.Wal;

sealed record GetCloudWalBacklogRuntimeRequest(ulong? ThroughSegmentId) : WalRuntimeRequest;
