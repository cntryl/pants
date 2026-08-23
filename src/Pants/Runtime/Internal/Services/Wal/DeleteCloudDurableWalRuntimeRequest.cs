namespace Cntryl.Pants;

sealed record DeleteCloudDurableWalRuntimeRequest(
    SealedWalSegment Segment) : WalRuntimeRequest;
