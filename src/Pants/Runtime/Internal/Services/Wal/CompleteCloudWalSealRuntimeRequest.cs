namespace Pants;

sealed record CompleteCloudWalSealRuntimeRequest(
    SealedWalSegment Segment) : WalRuntimeRequest;
