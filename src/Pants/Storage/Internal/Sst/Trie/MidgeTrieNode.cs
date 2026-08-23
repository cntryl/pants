namespace Cntryl.Pants.Storage.Internal.Sst.Trie;

sealed class MidgeTrieNode(
    ushort prefixLength,
    byte[] keyDelta,
    uint? blockId)
{
    public ushort PrefixLength { get; } = prefixLength;

    public byte[] KeyDelta { get; set; } = keyDelta;

    public uint? BlockId { get; set; } = blockId;

    public List<MidgeTrieEdge> Children { get; set; } = [];

    public void AddChild(MidgeTrieEdge edge)
    {
        var index = Children.BinarySearch(
            edge,
            Comparer<MidgeTrieEdge>.Create(static (left, right) =>
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

    public bool TryGetChild(byte firstByte, out MidgeTrieEdge edge)
    {
        var index = Children.BinarySearch(
            new MidgeTrieEdge(firstByte, 0),
            Comparer<MidgeTrieEdge>.Create(static (left, right) =>
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
