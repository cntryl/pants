namespace Pants;

sealed record DeleteCloudDurableWalRuntimeRequest(
    SealedWalSegment Segment) : WalRuntimeRequest;
