namespace Cntryl.Pants;

internal sealed record CloudObject(
    ReadOnlyMemory<byte> Data,
    string Version);
