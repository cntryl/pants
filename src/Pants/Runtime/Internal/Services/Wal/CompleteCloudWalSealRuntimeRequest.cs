namespace Cntryl.Pants;

sealed record CompleteCloudWalSealRuntimeRequest(
    SealedWalSegment Segment) : WalRuntimeRequest;
