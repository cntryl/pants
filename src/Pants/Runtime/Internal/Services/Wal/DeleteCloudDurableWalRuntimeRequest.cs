namespace Cntryl.Pants.Runtime.Internal.Services.Wal;

sealed record DeleteCloudDurableWalRuntimeRequest(
    SealedWalSegment Segment) : WalRuntimeRequest;
