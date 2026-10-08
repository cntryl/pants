using System.Buffers.Binary;

namespace Cntryl.Pants.Transactions.Internal.Spill;

/// <summary>
///     Writes, searches and validates the interval tree that indexes a spill run's delete-range
///     operations.
/// </summary>
/// <remarks>
///     Each node frame holds its own range's bounds and, instead of an inline copy of the subtree's
///     maximum end, the index of the node that owns that end. A node frame is therefore never larger
///     than its own delete-range operation, which admission already holds within the frame limit;
///     an inline copy let one large range inflate an unrelated node past it. The file is private to
///     the owning transaction and never read by another engine or after restart.
/// </remarks>
static class TransactionSpillRangeIndex
{
    internal const ulong NoChild = ulong.MaxValue;

    const int HeaderLength = 32;
    const int TableEntryLength = 12;
    const uint FormatVersion = 2;
    const int NodeFixedFieldBytes = 4 * sizeof(ulong);

    static ReadOnlySpan<byte> Magic => "MDGRNG01"u8;

    public static void Write(string path, IReadOnlyList<TransactionIntentOperation> sortedOperations)
    {
        var nodes = BuildNodes(sortedOperations);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            16 * 1024,
            FileOptions.None);
        stream.Write(new byte[HeaderLength + checked(nodes.Length * TableEntryLength)]);
        var nodeSectionOffset = checked((ulong)stream.Position);
        var offsets = new ulong[nodes.Length];
        for (var index = 0; index < nodes.Length; index++)
        {
            offsets[index] = checked((ulong)stream.Position);
            WriteNodeFrame(stream, nodes[index]);
        }

        stream.Position = HeaderLength;
        var entry = new byte[TableEntryLength];
        foreach (var offset in offsets)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(entry, offset);
            BinaryPrimitives.WriteUInt32LittleEndian(
                entry.AsSpan(8),
                DiskFormat.Crc32C(entry.AsSpan(0, 8)));
            stream.Write(entry);
        }

        Span<byte> header = stackalloc byte[HeaderLength];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], FormatVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(header[12..], checked((ulong)nodes.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(header[20..], nodeSectionOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], DiskFormat.Crc32C(header[..28]));
        stream.Position = 0;
        stream.Write(header);
        stream.Flush(true);
    }

    public static void Lookup(
        string path,
        ulong ordinalCeiling,
        ReadOnlySpan<byte> key,
        ref TransactionIntentLookup? latest)
    {
        using var stream = OpenRead(path);
        var (nodeCount, nodeSectionOffset) = ReadHeader(stream);
        var streamLength = checked((ulong)stream.Length);
        if (nodeCount > int.MaxValue ||
            nodeSectionOffset != checked(HeaderLength + nodeCount * TableEntryLength) ||
            nodeSectionOffset > streamLength)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction range index metadata is inconsistent.");
        }

        if (nodeCount == 0)
        {
            return;
        }

        var reader = new NodeReader(stream, nodeCount, nodeSectionOffset, streamLength);
        LookupSubtree(reader, 0, null, nodeCount, ordinalCeiling, key, ref latest);
    }

    /// <summary>
    ///     Validates the whole index, passing each node to <paramref name="validateNode" /> so the
    ///     caller can confirm it matches a delete-range operation in the run.
    /// </summary>
    public static void Validate(string path, Action<TransactionSpillRangeNode> validateNode)
    {
        using var stream = OpenRead(path);
        var (nodeCount, nodeSectionOffset) = ReadHeader(stream);
        var streamLength = checked((ulong)stream.Length);
        if (nodeCount > int.MaxValue ||
            nodeCount > (streamLength - Math.Min(streamLength, HeaderLength)) / TableEntryLength)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction range node count exceeds the file bounds.");
        }

        var expectedNodeSectionOffset = HeaderLength + nodeCount * TableEntryLength;
        if (nodeSectionOffset != expectedNodeSectionOffset || nodeSectionOffset > streamLength)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction range node section offset is invalid.");
        }

        ulong? previousNodeEnd = null;
        Span<byte> entry = stackalloc byte[TableEntryLength];
        for (var index = 0UL; index < nodeCount; index++)
        {
            stream.Position = checked((long)(HeaderLength + index * TableEntryLength));
            TransactionSpillStore.ReadExactly(stream, entry, "Transaction range offset entry is truncated.");
            if (DiskFormat.Crc32C(entry[..8]) != BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]))
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "Transaction range offset checksum does not match.");
            }

            var nodeOffset = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            var expectedOffset = previousNodeEnd ?? nodeSectionOffset;
            if (nodeOffset != expectedOffset || nodeOffset >= streamLength)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "Transaction range node offset is out of bounds.");
            }

            stream.Position = checked((long)nodeOffset);
            validateNode(ReadNodeFrame(stream, nodeCount));
            previousNodeEnd = checked((ulong)stream.Position);
        }

        if ((previousNodeEnd ?? nodeSectionOffset) != streamLength)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction range node section has an invalid extent.");
        }

        if (nodeCount != 0)
        {
            ValidateGraph(new NodeReader(stream, nodeCount, nodeSectionOffset, streamLength));
        }
    }

    static FileStream OpenRead(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1_024,
            FileOptions.RandomAccess);

    static (ulong NodeCount, ulong NodeSectionOffset) ReadHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        TransactionSpillStore.ReadExactly(stream, header, "Transaction range index header is truncated.");
        if (!header[..8].SequenceEqual(Magic) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != FormatVersion ||
            DiskFormat.Crc32C(header[..28]) != BinaryPrimitives.ReadUInt32LittleEndian(header[28..]))
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction range index header is invalid.");
        }

        return (
            BinaryPrimitives.ReadUInt64LittleEndian(header[12..]),
            BinaryPrimitives.ReadUInt64LittleEndian(header[20..]));
    }

    static void LookupSubtree(
        NodeReader reader,
        ulong nodeIndex,
        TransactionSpillRangeNode? loaded,
        ulong remainingNodes,
        ulong ordinalCeiling,
        ReadOnlySpan<byte> key,
        ref TransactionIntentLookup? latest)
    {
        if (remainingNodes == 0)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction range index contains a child cycle.");
        }

        var node = loaded ?? reader.Read(nodeIndex);
        if (node.Left != NoChild)
        {
            var left = reader.Read(node.Left);
            if (key.SequenceCompareTo(reader.ResolveMaximumEnd(node.Left, left)) < 0)
            {
                LookupSubtree(reader, node.Left, left, remainingNodes - 1, ordinalCeiling, key, ref latest);
            }
        }

        if (node.Ordinal < ordinalCeiling &&
            node.Start.AsSpan().SequenceCompareTo(key) <= 0 &&
            key.SequenceCompareTo(node.End) < 0 &&
            (latest is null || node.Ordinal > latest.Ordinal))
        {
            latest = new TransactionIntentLookup(node.Ordinal, null, true);
        }

        if (node.Right != NoChild && node.Start.AsSpan().SequenceCompareTo(key) <= 0)
        {
            LookupSubtree(reader, node.Right, null, remainingNodes - 1, ordinalCeiling, key, ref latest);
        }
    }

    static void ValidateGraph(NodeReader reader)
    {
        var nodeCount = checked((int)reader.NodeCount);
        var visited = new byte[nodeCount];
        var pending = new Stack<(ulong NodeIndex, byte[]? LowerBound, byte[]? UpperBound)>();
        pending.Push((0, null, null));
        var visitedCount = 0;
        while (pending.TryPop(out var pendingNode))
        {
            var (nodeIndex, lowerBound, upperBound) = pendingNode;
            var index = checked((int)nodeIndex);
            if (visited[index] != 0)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "Transaction range index contains a cycle or shared child.");
            }

            visited[index] = 1;
            visitedCount++;
            var node = reader.Read(nodeIndex);
            if ((lowerBound is not null &&
                 ByteArrayComparer.Instance.Compare(node.Start, lowerBound) < 0) ||
                (upperBound is not null &&
                 ByteArrayComparer.Instance.Compare(node.Start, upperBound) > 0))
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "Transaction range index violates an ancestor ordering bound.");
            }

            var expectedMaximumEnd = node.End;
            ValidateChild(node.Left, true);
            ValidateChild(node.Right, false);
            if (!expectedMaximumEnd.AsSpan().SequenceEqual(reader.ResolveMaximumEnd(nodeIndex, node)))
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "Transaction range subtree maximum is invalid.");
            }

            void ValidateChild(ulong childIndex, bool isLeft)
            {
                if (childIndex == NoChild)
                {
                    return;
                }

                var child = reader.Read(childIndex);
                var ordering = ByteArrayComparer.Instance.Compare(child.Start, node.Start);
                if ((isLeft && ordering > 0) || (!isLeft && ordering < 0))
                {
                    throw PantsException.Create(
                        PantsErrorCode.Corruption,
                        "Transaction range index ordering is invalid.");
                }

                var childMaximumEnd = reader.ResolveMaximumEnd(childIndex, child);
                if (ByteArrayComparer.Instance.Compare(childMaximumEnd, expectedMaximumEnd) > 0)
                {
                    expectedMaximumEnd = childMaximumEnd;
                }

                pending.Push(isLeft
                    ? (childIndex, lowerBound, node.Start)
                    : (childIndex, node.Start, upperBound));
            }
        }

        if (visitedCount != nodeCount)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction range index contains disconnected nodes.");
        }
    }

    static TransactionSpillRangeNode ReadNodeFrame(Stream stream, ulong nodeCount)
    {
        var payload = TransactionSpillStore.ReadFrame(stream);
        if (payload.Length < NodeFixedFieldBytes + 2 * sizeof(uint))
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "Transaction range node is truncated.");
        }

        var left = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(8));
        var right = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(16));
        var maximumEndNode = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(24));
        if ((left != NoChild && left >= nodeCount) ||
            (right != NoChild && right >= nodeCount) ||
            maximumEndNode >= nodeCount)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction range child is out of bounds.");
        }

        var cursor = NodeFixedFieldBytes;
        var start = TransactionSpillStore.ReadField(payload, ref cursor);
        var end = TransactionSpillStore.ReadField(payload, ref cursor);
        if (cursor != payload.Length || ByteArrayComparer.Instance.Compare(start, end) > 0)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction range node payload is invalid.");
        }

        return new TransactionSpillRangeNode(
            BinaryPrimitives.ReadUInt64LittleEndian(payload),
            left,
            right,
            start,
            end,
            maximumEndNode);
    }

    static TransactionSpillRangeNode[] BuildNodes(IReadOnlyList<TransactionIntentOperation> operations)
    {
        var operationIndexes = operations
            .Select(static (operation, index) => (operation, index))
            .Where(static item => item.operation.Kind == CommitOperationKind.DeleteRange)
            .Select(static item => item.index)
            .ToArray();
        var nodes = new List<TransactionSpillRangeNode>(operationIndexes.Length);
        BuildSubtree(operationIndexes, operations, nodes);
        return nodes.ToArray();
    }

    static ulong? BuildSubtree(
        ReadOnlySpan<int> operationIndexes,
        IReadOnlyList<TransactionIntentOperation> operations,
        List<TransactionSpillRangeNode> nodes)
    {
        if (operationIndexes.IsEmpty)
        {
            return null;
        }

        var middle = operationIndexes.Length / 2;
        var operation = operations[operationIndexes[middle]];
        var nodeIndex = nodes.Count;
        var node = new TransactionSpillRangeNode(
            operation.Ordinal,
            NoChild,
            NoChild,
            operation.Key,
            operation.EndExclusive!,
            checked((ulong)nodeIndex));
        nodes.Add(node);
        var left = BuildSubtree(operationIndexes[..middle], operations, nodes);
        var right = BuildSubtree(operationIndexes[(middle + 1)..], operations, nodes);
        var maximumEndNode = node.MaximumEndNode;
        foreach (var child in new[] { left, right }.OfType<ulong>())
        {
            var childMaximumEndNode = nodes[checked((int)child)].MaximumEndNode;
            if (ByteArrayComparer.Instance.Compare(
                    nodes[checked((int)childMaximumEndNode)].End,
                    nodes[checked((int)maximumEndNode)].End) > 0)
            {
                maximumEndNode = childMaximumEndNode;
            }
        }

        nodes[nodeIndex] = node with
        {
            Left = left ?? NoChild,
            Right = right ?? NoChild,
            MaximumEndNode = maximumEndNode
        };
        return checked((ulong)nodeIndex);
    }

    static void WriteNodeFrame(Stream stream, TransactionSpillRangeNode node)
    {
        using var payload = new MemoryStream();
        DiskFormat.WriteUInt64(payload, node.Ordinal);
        DiskFormat.WriteUInt64(payload, node.Left);
        DiskFormat.WriteUInt64(payload, node.Right);
        DiskFormat.WriteUInt64(payload, node.MaximumEndNode);
        TransactionSpillStore.WriteLength(payload, node.Start.Length);
        payload.Write(node.Start);
        TransactionSpillStore.WriteLength(payload, node.End.Length);
        payload.Write(node.End);
        TransactionSpillStore.WriteFrame(stream, payload.GetBuffer().AsSpan(0, checked((int)payload.Length)));
    }

    sealed class NodeReader(Stream stream, ulong nodeCount, ulong nodeSectionOffset, ulong streamLength)
    {
        public ulong NodeCount => nodeCount;

        public TransactionSpillRangeNode Read(ulong nodeIndex)
        {
            if (nodeIndex >= nodeCount)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "Transaction range child is out of bounds.");
            }

            stream.Position = checked((long)(HeaderLength + nodeIndex * TableEntryLength));
            Span<byte> entry = stackalloc byte[TableEntryLength];
            TransactionSpillStore.ReadExactly(stream, entry, "Transaction range offset entry is truncated.");
            if (DiskFormat.Crc32C(entry[..8]) != BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]))
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "Transaction range offset checksum does not match.");
            }

            var nodeOffset = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            if (nodeOffset < nodeSectionOffset || nodeOffset >= streamLength)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "Transaction range node offset is out of bounds.");
            }

            stream.Position = checked((long)nodeOffset);
            return ReadNodeFrame(stream, nodeCount);
        }

        /// <summary>Returns the end of the range that bounds <paramref name="node" />'s subtree.</summary>
        public byte[] ResolveMaximumEnd(ulong nodeIndex, TransactionSpillRangeNode node) =>
            node.MaximumEndNode == nodeIndex ? node.End : Read(node.MaximumEndNode).End;
    }
}
