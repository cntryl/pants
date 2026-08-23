namespace Cntryl.Pants.Storage.Internal.Sst;

sealed record MidgeSstMetadata(
    MidgeSstIndexKind IndexKind,
    MidgeSstBlockHandle? RangeHandle,
    byte[]? SmallestKey,
    byte[]? LargestKey);
