namespace Cntryl.Pants.Storage;

public sealed record PantsStorageFileLayout(
    string Name,
    int Level,
    uint ColumnFamilyId,
    long SizeBytes,
    ReadOnlyMemory<byte>? SmallestKey,
    ReadOnlyMemory<byte>? LargestKey,
    long? SmallestSequence,
    long? LargestSequence);
