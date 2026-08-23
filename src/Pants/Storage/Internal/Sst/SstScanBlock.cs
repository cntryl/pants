namespace Pants;

internal sealed class SstScanBlock
{
    public SstScanBlock(
        MidgeSstReader reader,
        int blockIndex,
        byte[] firstKey,
        byte[]? nextFirstKey)
    {
        Reader = reader;
        BlockIndex = blockIndex;
        FirstKey = firstKey;
        NextFirstKey = nextFirstKey;
    }

    public MidgeSstReader Reader { get; }

    public int BlockIndex { get; }

    public byte[] FirstKey { get; }

    public byte[]? NextFirstKey { get; }

    public bool IsValidated { get; set; }

    public bool Contains(ReadOnlySpan<byte> key) =>
        key.SequenceCompareTo(FirstKey) >= 0 &&
        (NextFirstKey is null || key.SequenceCompareTo(NextFirstKey) < 0);
}
