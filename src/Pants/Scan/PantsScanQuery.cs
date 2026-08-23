namespace Cntryl.Pants;

public sealed record PantsScanQuery
{
    public ReadOnlyMemory<byte>? StartInclusive { get; init; }

    public ReadOnlyMemory<byte>? EndExclusive { get; init; }

    public ReadOnlyMemory<byte>? Prefix { get; init; }

    public PantsScanDirection Direction { get; init; }

    public int? Limit { get; init; }
}
