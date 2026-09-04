namespace Cntryl.Pants.Storage.Internal.Sst;

sealed record SstMetadata(
    SstIndexKind IndexKind,
    SstBlockHandle? RangeHandle,
    byte[]? SmallestKey,
    byte[]? LargestKey);
