namespace Cntryl.Pants.Observability;

public sealed record PantsPointReadResult(
    ReadOnlyMemory<byte>? Value,
    PantsPointReadTrace Trace);
