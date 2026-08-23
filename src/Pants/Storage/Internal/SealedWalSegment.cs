namespace Cntryl.Pants.Storage.Internal;

sealed record SealedWalSegment(
    ulong SegmentId,
    ulong WriterEpoch,
    ulong MaximumSequence,
    string FileName,
    byte[] Bytes);
