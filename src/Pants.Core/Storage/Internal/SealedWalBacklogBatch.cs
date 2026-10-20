namespace Cntryl.Pants.Storage.Internal;

sealed record SealedWalBacklogBatch(
    IReadOnlyList<SealedWalSegment> Segments,
    ulong? ThroughSegmentId)
{
    public const int DefaultMaximumSegments = 32;
    public const long DefaultMaximumBytes = 64L * 1024 * 1024;
}
