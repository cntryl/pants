namespace Cntryl.Pants.Storage.Internal.Sst.Trie;

sealed class TrieNode(
    ushort prefixLength,
    byte[] keyDelta,
    uint? blockId)
{
    public ushort PrefixLength { get; } = prefixLength;

    public byte[] KeyDelta { get; set; } = keyDelta;

    public uint? BlockId { get; set; } = blockId;

    public List<TrieEdge> Children { get; set; } = [];

    public void AddChild(TrieEdge edge)
    {
        var index = Children.BinarySearch(
            edge,
            Comparer<TrieEdge>.Create(static (left, right) =>
                left.FirstByte.CompareTo(right.FirstByte)));
        if (index >= 0)
        {
            Children[index] = edge;
        }
        else
        {
            Children.Insert(~index, edge);
        }
    }

    public bool TryGetChild(byte firstByte, out TrieEdge edge)
    {
        var index = Children.BinarySearch(
            new TrieEdge(firstByte, 0),
            Comparer<TrieEdge>.Create(static (left, right) =>
                left.FirstByte.CompareTo(right.FirstByte)));
        if (index >= 0)
        {
            edge = Children[index];
            return true;
        }

        edge = default;
        return false;
    }
}
