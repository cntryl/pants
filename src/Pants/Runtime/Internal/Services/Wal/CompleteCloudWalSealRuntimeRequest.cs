namespace Cntryl.Pants.Runtime.Internal.Services.Wal;

sealed record CompleteCloudWalSealRuntimeRequest(
    SealedWalSegment Segment) : WalRuntimeRequest;
