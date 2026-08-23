namespace Cntryl.Pants;

sealed class WalCloudSealCompletedException : Exception
{
    public WalCloudSealCompletedException(
        SealedWalSegment segment,
        Exception boundaryFailure)
        : base(
            "The cloud WAL rotation completed before a later boundary failed.",
            boundaryFailure)
    {
        Segment = segment;
    }

    public SealedWalSegment Segment { get; }
}
