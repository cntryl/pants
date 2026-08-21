namespace Pants;

internal sealed record SealedWalSegment(
    ulong SegmentId,
    ulong WriterEpoch,
    ulong MaximumSequence,
    string FileName,
    byte[] Bytes);
