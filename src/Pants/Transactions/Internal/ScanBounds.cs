namespace Cntryl.Pants.Transactions.Internal;

sealed class ScanBounds
{
    public ScanBounds(PantsScanQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        Direction = query.Direction;
        Prefix = Copy(query.Prefix);
        StartInclusive = Maximum(Copy(query.StartInclusive), Prefix);
        EndExclusive = Minimum(Copy(query.EndExclusive), PrefixSuccessor(Prefix));
        Limit = query.Limit ?? int.MaxValue;
        if (!Enum.IsDefined(Direction))
        {
            throw PantsException.InvalidArgument("Scan direction is invalid.");
        }

        if (Limit < 0)
        {
            throw PantsException.InvalidArgument("Scan limit must not be negative.");
        }

        if (query.StartInclusive is { } start && query.EndExclusive is { } end &&
            start.Span.SequenceCompareTo(end.Span) > 0)
        {
            throw PantsException.InvalidArgument(
                "Scan requires StartInclusive to be less than or equal to EndExclusive.");
        }
    }

    public byte[]? StartInclusive { get; }

    public byte[]? EndExclusive { get; }

    public byte[]? Prefix { get; }

    public PantsScanDirection Direction { get; }

    public int Limit { get; }

    public bool Matches(ReadOnlySpan<byte> key) =>
        (StartInclusive is null || key.SequenceCompareTo(StartInclusive) >= 0) &&
        (EndExclusive is null || key.SequenceCompareTo(EndExclusive) < 0) &&
        (Prefix is null || key.StartsWith(Prefix));

    public bool Overlaps(ReadOnlySpan<byte> smallest, ReadOnlySpan<byte> largest) =>
        (EndExclusive is null || smallest.SequenceCompareTo(EndExclusive) < 0) &&
        (StartInclusive is null || largest.SequenceCompareTo(StartInclusive) >= 0);

    static byte[]? Maximum(byte[]? left, byte[]? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left.AsSpan().SequenceCompareTo(right) >= 0 ? left : right;
    }

    static byte[]? Minimum(byte[]? left, byte[]? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left.AsSpan().SequenceCompareTo(right) <= 0 ? left : right;
    }

    static byte[]? PrefixSuccessor(byte[]? prefix)
    {
        if (prefix is null || prefix.Length == 0)
        {
            return null;
        }

        var successor = prefix.ToArray();
        for (var index = successor.Length - 1; index >= 0; index--)
        {
            if (successor[index] == byte.MaxValue)
            {
                continue;
            }

            successor[index]++;
            return successor[..(index + 1)];
        }

        return null;
    }

    static byte[]? Copy(ReadOnlyMemory<byte>? value) =>
        value.HasValue ? value.Value.ToArray() : null;
}
