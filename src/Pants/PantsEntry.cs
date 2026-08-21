namespace Pants;

public readonly record struct PantsEntry(ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte> Value);
