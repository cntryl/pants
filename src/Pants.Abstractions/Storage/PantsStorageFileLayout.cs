namespace Cntryl.Pants.Storage;

public sealed record PantsStorageFileLayout(
    string Name,
    int Level,
    uint ColumnFamilyId,
    long SizeBytes,
    ReadOnlyMemory<byte>? SmallestKey,
    ReadOnlyMemory<byte>? LargestKey,
    ulong? SmallestSequence,
    ulong? LargestSequence);
