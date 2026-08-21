namespace Pants;

internal sealed record MidgeSstMetadata(
    MidgeSstIndexKind IndexKind,
    MidgeSstBlockHandle? RangeHandle,
    byte[]? SmallestKey,
    byte[]? LargestKey);
