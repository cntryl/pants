namespace Cntryl.Pants;

internal sealed class MidgeTrieIndex
{
    private readonly KeyValuePair<byte[], int>[] _entries;

    private MidgeTrieIndex(KeyValuePair<byte[], int>[] entries)
    {
        _entries = entries;
    }

    public static byte[] Encode(IReadOnlyList<byte[]> blockFirstKeys)
    {
        ArgumentNullException.ThrowIfNull(blockFirstKeys);
        var builder = new MidgeTrieBuilder();
        for (var index = 0; index < blockFirstKeys.Count; index++)
        {
            builder.Add(blockFirstKeys[index], checked((uint)index));
        }

        using var output = new MemoryStream();
        IReadOnlyList<MidgeTrieNode> nodes = builder.Finish();
        WriteVarint(output, checked((ulong)nodes.Count));
        foreach (MidgeTrieNode node in nodes)
        {
            WriteVarint(output, node.PrefixLength);
            WriteVarint(output, checked((ulong)node.KeyDelta.Length));
            output.Write(node.KeyDelta);
            WriteVarint(output, node.BlockId.HasValue ? checked(node.BlockId.Value + 1UL) : 0);
            WriteVarint(output, checked((ulong)node.Children.Count));
            foreach (MidgeTrieEdge child in node.Children)
            {
                output.WriteByte(child.FirstByte);
                WriteVarint(output, child.ChildIndex);
            }
        }

        return output.ToArray();
    }

    public static MidgeTrieIndex Decode(
        ReadOnlySpan<byte> bytes,
        IReadOnlyList<byte[]> blockFirstKeys)
    {
        var cursor = 0;
        int nodeCount = ReadInt32(bytes, ref cursor, "node count");
        if (nodeCount <= 0 || nodeCount > (bytes.Length - cursor) / 4)
        {
            throw new PantsStorageException("SST trie node count is invalid.");
        }

        var nodes = new MidgeTrieNode[nodeCount];
        for (var index = 0; index < nodeCount; index++)
        {
            ushort prefixLength = ReadUInt16(bytes, ref cursor, "prefix length");
            int keyLength = ReadInt32(bytes, ref cursor, "key delta length");
            if (keyLength < 0 || keyLength > bytes.Length - cursor)
            {
                throw new PantsStorageException("SST trie key delta is truncated.");
            }

            byte[] keyDelta = bytes.Slice(cursor, keyLength).ToArray();
            cursor += keyLength;
            ulong rawBlockId = ReadVarint(bytes, ref cursor);
            uint? blockId = rawBlockId switch
            {
                0 => null,
                > (ulong)uint.MaxValue => throw new PantsStorageException(
                    "SST trie block ID exceeds UInt32."),
                _ => (uint)(rawBlockId - 1)
            };
            int childCount = ReadInt32(bytes, ref cursor, "child count");
            if (childCount < 0 || childCount > (bytes.Length - cursor) / 2)
            {
                throw new PantsStorageException("SST trie child count is invalid.");
            }

            var node = new MidgeTrieNode(prefixLength, keyDelta, blockId);
            for (var child = 0; child < childCount; child++)
            {
                if (cursor >= bytes.Length)
                {
                    throw new PantsStorageException("SST trie child edge is truncated.");
                }

                byte firstByte = bytes[cursor++];
                uint childIndex = ReadUInt32(bytes, ref cursor, "child index");
                node.AddChild(new MidgeTrieEdge(firstByte, childIndex));
            }

            nodes[index] = node;
        }

        if (cursor != bytes.Length)
        {
            throw new PantsStorageException("SST trie has trailing bytes.");
        }

        var entries = new List<KeyValuePair<byte[], int>>(blockFirstKeys.Count);
        var inboundEdges = new int[nodeCount];
        ValidateGraph(nodes, inboundEdges);
        CollectEntries(nodes, entries, blockFirstKeys.Count);

        entries.Sort(static (left, right) => ByteArrayComparer.Instance.Compare(left.Key, right.Key));
        foreach (KeyValuePair<byte[], int> entry in entries)
        {
            if (!entry.Key.AsSpan().SequenceEqual(blockFirstKeys[entry.Value]))
            {
                throw new PantsStorageException("SST trie does not match the binary block index.");
            }
        }

        return new MidgeTrieIndex(entries.ToArray());
    }

    public int FindFloorBlock(ReadOnlySpan<byte> key)
    {
        var low = 0;
        int high = _entries.Length - 1;
        var result = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            int comparison = key.SequenceCompareTo(_entries[middle].Key);
            if (comparison < 0)
            {
                high = middle - 1;
            }
            else
            {
                result = _entries[middle].Value;
                low = middle + 1;
            }
        }

        return result;
    }

    private static void CollectEntries(
        MidgeTrieNode[] nodes,
        List<KeyValuePair<byte[], int>> entries,
        int blockCount)
    {
        var visited = new bool[nodes.Length];
        var pending = new Stack<(int NodeIndex, byte[] Prefix)>();
        pending.Push((0, []));
        while (pending.TryPop(out (int NodeIndex, byte[] Prefix) current))
        {
            if (visited[current.NodeIndex])
            {
                throw new PantsStorageException("SST trie graph contains a cycle or shared child.");
            }

            visited[current.NodeIndex] = true;
            MidgeTrieNode node = nodes[current.NodeIndex];
            byte[] key = new byte[checked(current.Prefix.Length + node.KeyDelta.Length)];
            current.Prefix.CopyTo(key, 0);
            node.KeyDelta.CopyTo(key, current.Prefix.Length);
            if (node.BlockId is { } blockId)
            {
                if (blockId >= blockCount)
                {
                    throw new PantsStorageException("SST trie block ID is outside the binary index.");
                }

                entries.Add(new KeyValuePair<byte[], int>(key, checked((int)blockId)));
            }

            for (int childPosition = node.Children.Count - 1; childPosition >= 0; childPosition--)
            {
                MidgeTrieEdge edge = node.Children[childPosition];
                int childIndex = checked((int)edge.ChildIndex);
                if (nodes[childIndex].KeyDelta[0] != edge.FirstByte)
                {
                    throw new PantsStorageException("SST trie child edge is inconsistent.");
                }

                pending.Push((childIndex, key));
            }
        }

        if (visited.Contains(false))
        {
            throw new PantsStorageException("SST trie is disconnected.");
        }
    }

    private static void ValidateGraph(
        MidgeTrieNode[] nodes,
        Span<int> inboundEdges)
    {
        for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
        {
            MidgeTrieNode node = nodes[nodeIndex];
            if (nodeIndex == 0)
            {
                if (node.PrefixLength != 0 || node.KeyDelta.Length != 0)
                {
                    throw new PantsStorageException("SST trie root node is invalid.");
                }
            }
            else if (node.KeyDelta.Length == 0)
            {
                throw new PantsStorageException("SST trie contains an empty non-root key delta.");
            }

            for (var childPosition = 0; childPosition < node.Children.Count; childPosition++)
            {
                MidgeTrieEdge edge = node.Children[childPosition];
                if (childPosition > 0 &&
                    node.Children[childPosition - 1].FirstByte >= edge.FirstByte)
                {
                    throw new PantsStorageException(
                        "SST trie child edges are duplicated or unsorted.");
                }

                if (edge.ChildIndex >= (uint)nodes.Length)
                {
                    throw new PantsStorageException("SST trie references a missing child.");
                }

                inboundEdges[checked((int)edge.ChildIndex)] = checked(
                    inboundEdges[checked((int)edge.ChildIndex)] + 1);
            }
        }

        if (inboundEdges[0] != 0 ||
            inboundEdges[1..].ContainsAnyExcept(1))
        {
            throw new PantsStorageException(
                "SST trie graph is cyclic, shared, or disconnected.");
        }
    }

    private static void WriteVarint(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }

        output.WriteByte((byte)value);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> bytes, ref int cursor)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (cursor >= bytes.Length)
            {
                throw new PantsStorageException("SST trie varint is truncated.");
            }

            byte value = bytes[cursor++];
            result |= (ulong)(value & 0x7f) << shift;
            if ((value & 0x80) == 0)
            {
                return result;
            }
        }

        throw new PantsStorageException("SST trie varint overflows 64 bits.");
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, ref int cursor, string field)
    {
        ulong value = ReadVarint(bytes, ref cursor);
        if (value > int.MaxValue)
        {
            throw new PantsStorageException($"SST trie {field} exceeds Int32.");
        }

        return (int)value;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, ref int cursor, string field)
    {
        ulong value = ReadVarint(bytes, ref cursor);
        if (value > uint.MaxValue)
        {
            throw new PantsStorageException($"SST trie {field} exceeds UInt32.");
        }

        return (uint)value;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, ref int cursor, string field)
    {
        ulong value = ReadVarint(bytes, ref cursor);
        if (value > ushort.MaxValue)
        {
            throw new PantsStorageException($"SST trie {field} exceeds UInt16.");
        }

        return (ushort)value;
    }
}
