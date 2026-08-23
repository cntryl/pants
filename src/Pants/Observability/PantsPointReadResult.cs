namespace Pants;

public sealed record PantsPointReadResult(
    ReadOnlyMemory<byte>? Value,
    PantsPointReadTrace Trace);
