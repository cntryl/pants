namespace Cntryl.Pants;

internal sealed class MidgeTrieBuilder
{
    private readonly List<MidgeTrieNode> _nodes = [new(0, [], null)];
    private byte[] _lastKey = [];

    public void Add(ReadOnlySpan<byte> key, uint blockId)
    {
        if (key.IsEmpty)
        {
            return;
        }

        if (_lastKey.Length != 0 && key.SequenceCompareTo(_lastKey) < 0)
        {
            throw new PantsStorageException("Trie block keys must be added in sorted order.");
        }

        if (blockId == uint.MaxValue)
        {
            throw new PantsStorageException("The maximum trie block ID is reserved.");
        }

        Insert(key, blockId);
        _lastKey = key.ToArray();
    }

    public IReadOnlyList<MidgeTrieNode> Finish() => _nodes;

    private void Insert(ReadOnlySpan<byte> key, uint blockId)
    {
        var currentIndex = 0;
        var matchedLength = 0;
        while (matchedLength < key.Length)
        {
            ReadOnlySpan<byte> remaining = key[matchedLength..];
            if (!_nodes[currentIndex].TryGetChild(remaining[0], out MidgeTrieEdge edge))
            {
                ushort prefixLength = checked((ushort)matchedLength);
                uint newIndex = checked((uint)_nodes.Count);
                _nodes.Add(new MidgeTrieNode(prefixLength, remaining.ToArray(), blockId));
                _nodes[currentIndex].AddChild(new MidgeTrieEdge(remaining[0], newIndex));
                return;
            }

            int childIndex = checked((int)edge.ChildIndex);
            MidgeTrieNode child = _nodes[childIndex];
            int childMatchLength = CommonPrefixLength(child.KeyDelta, remaining);
            if (childMatchLength == child.KeyDelta.Length)
            {
                matchedLength = checked(matchedLength + childMatchLength);
                currentIndex = childIndex;
                continue;
            }

            SplitNode(childIndex, childMatchLength, remaining, blockId);
            return;
        }

        _nodes[currentIndex].BlockId = blockId;
    }

    private void SplitNode(
        int nodeIndex,
        int splitPosition,
        ReadOnlySpan<byte> remaining,
        uint blockId)
    {
        MidgeTrieNode existing = _nodes[nodeIndex];
        byte[] oldSuffix = existing.KeyDelta[splitPosition..];
        ushort splitPrefixLength = checked((ushort)splitPosition);
        var intermediate = new MidgeTrieNode(
            existing.PrefixLength,
            existing.KeyDelta[..splitPosition],
            null);
        uint oldRemainderIndex = checked((uint)_nodes.Count);
        var oldRemainder = new MidgeTrieNode(
            splitPrefixLength,
            oldSuffix,
            existing.BlockId)
        {
            Children = [.. existing.Children]
        };
        _nodes.Add(oldRemainder);
        intermediate.AddChild(new MidgeTrieEdge(oldSuffix[0], oldRemainderIndex));

        byte[] newSuffix = remaining[splitPosition..].ToArray();
        if (newSuffix.Length == 0)
        {
            intermediate.BlockId = blockId;
        }
        else
        {
            uint newIndex = checked((uint)_nodes.Count);
            _nodes.Add(new MidgeTrieNode(splitPrefixLength, newSuffix, blockId));
            intermediate.AddChild(new MidgeTrieEdge(newSuffix[0], newIndex));
        }

        _nodes[nodeIndex] = intermediate;
    }

    private static int CommonPrefixLength(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int length = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < length && left[index] == right[index])
        {
            index++;
        }

        return index;
    }
}
