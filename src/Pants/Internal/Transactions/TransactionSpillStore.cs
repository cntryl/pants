using System.Buffers.Binary;

namespace Pants;

internal sealed class TransactionSpillStore : IDisposable
{
    private const int HeaderLength = 48;
    private const int SparseIndexStride = 16;
    private const int RangeHeaderLength = 32;
    private const int RangeTableEntryLength = 12;
    private const ulong NoRangeChild = ulong.MaxValue;

    static readonly Lock DirectoryMutationGate = new();

    private static ReadOnlySpan<byte> RunMagic => "MDGTXN01"u8;

    private static ReadOnlySpan<byte> RangeMagic => "MDGRNG01"u8;

    private readonly string _directory;
    private readonly ulong _transactionId;
    private readonly ColumnFamilyIdentity _family;
    private readonly List<SpillRun> _runs = [];
    private int _disposed;

    public TransactionSpillStore(
        string databasePath,
        long transactionId,
        ColumnFamilyIdentity family)
    {
        _directory = Path.Combine(databasePath, "txn");
        _transactionId = checked((ulong)transactionId);
        _family = family;
    }

    public bool HasRuns => _runs.Count != 0;

    public static void CleanupOrphans(string databasePath)
    {
        var directory = Path.Combine(databasePath, "txn");
        lock (DirectoryMutationGate)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    public void WriteRun(IReadOnlyList<TransactionIntentOperation> operations)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (operations.Count == 0)
        {
            return;
        }

        var runNumber = _runs.Count;
        var stem = $"{_transactionId:x16}-{runNumber:x8}";
        var runPath = Path.Combine(_directory, $"{stem}.run");
        var runTemporaryPath = $"{runPath}.tmp";
        var rangePath = Path.Combine(_directory, $"{stem}.ranges");
        var rangeTemporaryPath = $"{rangePath}.tmp";
        var sorted = operations
            .OrderBy(static operation => operation.Key, ByteArrayComparer.Instance)
            .ThenBy(static operation => operation.Ordinal)
            .ToArray();

        try
        {
            using (var stream = CreateRunTemporaryFile(runTemporaryPath))
            {
                WriteRunFile(stream, sorted);
            }

            WriteRangeFile(rangeTemporaryPath, sorted);
            File.Move(rangeTemporaryPath, rangePath);
            File.Move(runTemporaryPath, runPath);
            _runs.Add(new SpillRun(runPath, rangePath, sorted.Length));
        }
        catch (Exception exception) when (exception is not PantsException)
        {
            DeleteIfPresent(runTemporaryPath);
            DeleteIfPresent(rangeTemporaryPath);
            DeleteIfPresent(runPath);
            DeleteIfPresent(rangePath);
            throw PantsException.Create(
                PantsErrorCode.Io,
                "A transaction spill run could not be published.",
                exception);
        }
        catch
        {
            DeleteIfPresent(runTemporaryPath);
            DeleteIfPresent(rangeTemporaryPath);
            DeleteIfPresent(runPath);
            DeleteIfPresent(rangePath);
            throw;
        }
    }

    public IReadOnlyList<TransactionIntentOperation> ReadAll()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var operations = new List<TransactionIntentOperation>();
        foreach (SpillRun run in _runs)
        {
            operations.AddRange(ReadRun(run));
        }

        operations.Sort(static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
        return operations;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (SpillRun run in _runs)
        {
            TryDelete(run.Path);
            TryDelete(run.RangePath);
        }

        _runs.Clear();
        try
        {
            lock (DirectoryMutationGate)
            {
                if (Directory.Exists(_directory) && !Directory.EnumerateFileSystemEntries(_directory).Any())
                {
                    Directory.Delete(_directory);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another transaction may have populated the shared directory.
        }
    }

    FileStream CreateRunTemporaryFile(string path)
    {
        lock (DirectoryMutationGate)
        {
            Directory.CreateDirectory(_directory);
            return new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.None);
        }
    }

    static void WriteRunFile(FileStream stream, TransactionIntentOperation[] operations)
    {
        stream.Write(new byte[HeaderLength]);
        var ordinalOffsets = new List<(ulong Ordinal, ulong Offset)>(operations.Length);
        var sparseEntries = new List<(byte[] Key, ulong Offset)>(
            operations.Length / SparseIndexStride + 1);
        for (int index = 0; index < operations.Length; index++)
        {
            TransactionIntentOperation operation = operations[index];
            ulong offset = checked((ulong)stream.Position);
            WriteOperationFrame(stream, operation);
            ordinalOffsets.Add((operation.Ordinal, offset));
            if (index % SparseIndexStride == 0)
            {
                sparseEntries.Add((operation.Key, offset));
            }
        }

        ulong ordinalTableOffset = checked((ulong)stream.Position);
        var ordinalPayload = new byte[16];
        foreach ((ulong ordinal, ulong offset) in ordinalOffsets.OrderBy(static entry => entry.Ordinal))
        {
            BinaryPrimitives.WriteUInt64LittleEndian(ordinalPayload, ordinal);
            BinaryPrimitives.WriteUInt64LittleEndian(ordinalPayload.AsSpan(8), offset);
            WriteFrame(stream, ordinalPayload);
        }

        ulong sparseIndexOffset = checked((ulong)stream.Position);
        foreach ((byte[] key, ulong offset) in sparseEntries)
        {
            using var payload = new MemoryStream();
            WriteLength(payload, key.Length);
            payload.Write(key);
            MidgeDiskFormat.WriteUInt64(payload, offset);
            WriteFrame(stream, payload.GetBuffer().AsSpan(0, checked((int)payload.Length)));
        }

        Span<byte> header = stackalloc byte[HeaderLength];
        RunMagic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 2);
        BinaryPrimitives.WriteUInt64LittleEndian(header[12..], checked((ulong)operations.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(header[20..], ordinalTableOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[28..], sparseIndexOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[36..], checked((ulong)sparseEntries.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], MidgeDiskFormat.Crc32C(header[..44]));
        stream.Position = 0;
        stream.Write(header);
        stream.Flush(flushToDisk: true);
    }

    private List<TransactionIntentOperation> ReadRun(SpillRun run)
    {
        try
        {
            using var stream = new FileStream(
                run.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            Span<byte> header = stackalloc byte[HeaderLength];
            ReadExactly(stream, header, "Transaction spill header is truncated.");
            if (!header[..8].SequenceEqual(RunMagic) ||
                BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != 2 ||
                MidgeDiskFormat.Crc32C(header[..44]) != BinaryPrimitives.ReadUInt32LittleEndian(header[44..]))
            {
                throw PantsException.Create(PantsErrorCode.Corruption, "Transaction spill header is invalid.");
            }

            ulong recordCount = BinaryPrimitives.ReadUInt64LittleEndian(header[12..]);
            ulong ordinalTableOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[20..]);
            if (recordCount != checked((ulong)run.RecordCount) ||
                ordinalTableOffset < HeaderLength ||
                ordinalTableOffset >= checked((ulong)stream.Length))
            {
                throw PantsException.Create(PantsErrorCode.Corruption, "Transaction spill metadata is inconsistent.");
            }

            stream.Position = checked((long)ordinalTableOffset);
            var ordinalOffsets = new (ulong Ordinal, ulong Offset)[run.RecordCount];
            for (int index = 0; index < ordinalOffsets.Length; index++)
            {
                byte[] entry = ReadFrame(stream);
                if (entry.Length != 16)
                {
                    throw PantsException.Create(
                        PantsErrorCode.Corruption,
                        "Transaction spill ordinal entry has an invalid length.");
                }

                ordinalOffsets[index] = (
                    BinaryPrimitives.ReadUInt64LittleEndian(entry),
                    BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(8)));
            }

            var operations = new List<TransactionIntentOperation>(run.RecordCount);
            foreach ((ulong ordinal, ulong offset) in ordinalOffsets)
            {
                if (offset < HeaderLength || offset >= ordinalTableOffset)
                {
                    throw PantsException.Create(
                        PantsErrorCode.Corruption,
                        "Transaction spill operation offset is out of bounds.");
                }

                stream.Position = checked((long)offset);
                TransactionIntentOperation operation = ReadOperationFrame(stream, ordinal);
                operations.Add(operation);
            }

            return operations;
        }
        catch (PantsException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw PantsException.Create(
                PantsErrorCode.Io,
                "A transaction spill run could not be read.",
                exception);
        }
    }

    private static void WriteOperationFrame(Stream stream, TransactionIntentOperation operation)
    {
        using var payload = new MemoryStream();
        MidgeDiskFormat.WriteUInt64(payload, operation.Ordinal);
        payload.WriteByte(operation.Kind switch
        {
            CommitOperationKind.Put when operation.InsertOnly => 1,
            CommitOperationKind.Put => 0,
            CommitOperationKind.Delete => 2,
            CommitOperationKind.DeleteRange => 3,
            _ => throw PantsException.Create(PantsErrorCode.Internal, "Transaction intent kind is invalid.")
        });
        MidgeDiskFormat.WriteUInt32(payload, operation.Family.Id);
        payload.WriteByte(operation.TimeToLive.HasValue ? (byte)1 : (byte)0);
        MidgeDiskFormat.WriteUInt64(
            payload,
            operation.TimeToLive.HasValue
                ? checked((ulong)operation.TimeToLive.Value.TotalSeconds)
                : 0);
        WriteLength(payload, operation.Key.Length);
        payload.Write(operation.Key);
        byte[] second = operation.Kind switch
        {
            CommitOperationKind.Put => operation.Value ?? [],
            CommitOperationKind.DeleteRange => operation.EndExclusive ?? [],
            _ => []
        };
        WriteLength(payload, second.Length);
        payload.Write(second);
        WriteFrame(stream, payload.GetBuffer().AsSpan(0, checked((int)payload.Length)));
    }

    private TransactionIntentOperation ReadOperationFrame(Stream stream, ulong expectedOrdinal)
    {
        byte[] payload = ReadFrame(stream);
        if (payload.Length < 30)
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "Transaction spill operation is truncated.");
        }

        ReadOnlySpan<byte> bytes = payload;
        ulong ordinal = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        byte tag = bytes[8];
        uint familyId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]);
        byte ttlPresent = bytes[13];
        ulong ttlSeconds = BinaryPrimitives.ReadUInt64LittleEndian(bytes[14..]);
        if (ordinal != expectedOrdinal || familyId != _family.Id || ttlPresent > 1 ||
            (ttlPresent == 0 && ttlSeconds != 0) || (tag >= 2 && ttlPresent != 0))
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "Transaction spill operation metadata is invalid.");
        }

        int cursor = 22;
        byte[] key = ReadField(bytes, ref cursor);
        byte[] second = ReadField(bytes, ref cursor);
        if (cursor != bytes.Length || tag > 3 || (tag == 2 && second.Length != 0))
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "Transaction spill operation payload is invalid.");
        }

        TimeSpan? ttl = ttlPresent == 0
            ? null
            : TimeSpan.FromSeconds(checked((long)ttlSeconds));
        return tag switch
        {
            0 or 1 => new TransactionIntentOperation(
                ordinal,
                CommitOperationKind.Put,
                _family,
                key,
                null,
                second,
                ttl,
                null,
                tag == 1),
            2 => new TransactionIntentOperation(
                ordinal,
                CommitOperationKind.Delete,
                _family,
                key,
                null,
                null,
                null,
                null,
                false),
            3 => new TransactionIntentOperation(
                ordinal,
                CommitOperationKind.DeleteRange,
                _family,
                key,
                second,
                null,
                null,
                null,
                false),
            _ => throw PantsException.Create(PantsErrorCode.Corruption, "Transaction spill operation tag is invalid.")
        };
    }

    private static void WriteRangeFile(string path, IReadOnlyList<TransactionIntentOperation> operations)
    {
        RangeNode[] nodes = BuildRangeNodes(operations);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.None);
        stream.Write(new byte[RangeHeaderLength + checked(nodes.Length * RangeTableEntryLength)]);
        ulong nodeSectionOffset = checked((ulong)stream.Position);
        var offsets = new ulong[nodes.Length];
        for (int index = 0; index < nodes.Length; index++)
        {
            offsets[index] = checked((ulong)stream.Position);
            WriteRangeNodeFrame(stream, nodes[index]);
        }

        stream.Position = RangeHeaderLength;
        var entry = new byte[RangeTableEntryLength];
        foreach (ulong offset in offsets)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(entry, offset);
            BinaryPrimitives.WriteUInt32LittleEndian(
                entry.AsSpan(8),
                MidgeDiskFormat.Crc32C(entry.AsSpan(0, 8)));
            stream.Write(entry);
        }

        Span<byte> header = stackalloc byte[RangeHeaderLength];
        RangeMagic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header[12..], checked((ulong)nodes.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(header[20..], nodeSectionOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], MidgeDiskFormat.Crc32C(header[..28]));
        stream.Position = 0;
        stream.Write(header);
        stream.Flush(flushToDisk: true);
    }

    private static RangeNode[] BuildRangeNodes(IReadOnlyList<TransactionIntentOperation> operations)
    {
        int[] operationIndexes = operations
            .Select(static (operation, index) => (operation, index))
            .Where(static item => item.operation.Kind == CommitOperationKind.DeleteRange)
            .Select(static item => item.index)
            .ToArray();
        var nodes = new List<RangeNode>(operationIndexes.Length);
        BuildRangeSubtree(operationIndexes, operations, nodes);
        return nodes.ToArray();
    }

    private static ulong? BuildRangeSubtree(
        ReadOnlySpan<int> operationIndexes,
        IReadOnlyList<TransactionIntentOperation> operations,
        List<RangeNode> nodes)
    {
        if (operationIndexes.IsEmpty)
        {
            return null;
        }

        int middle = operationIndexes.Length / 2;
        TransactionIntentOperation operation = operations[operationIndexes[middle]];
        int nodeIndex = nodes.Count;
        var node = new RangeNode(
            operation.Ordinal,
            NoRangeChild,
            NoRangeChild,
            operation.Key,
            operation.EndExclusive!,
            operation.EndExclusive!);
        nodes.Add(node);
        ulong? left = BuildRangeSubtree(operationIndexes[..middle], operations, nodes);
        ulong? right = BuildRangeSubtree(operationIndexes[(middle + 1)..], operations, nodes);
        byte[] maximumEnd = node.End;
        foreach (ulong child in new[] { left, right }.OfType<ulong>())
        {
            if (ByteArrayComparer.Instance.Compare(nodes[checked((int)child)].MaximumEnd, maximumEnd) > 0)
            {
                maximumEnd = nodes[checked((int)child)].MaximumEnd;
            }
        }

        nodes[nodeIndex] = node with
        {
            Left = left ?? NoRangeChild,
            Right = right ?? NoRangeChild,
            MaximumEnd = maximumEnd
        };
        return checked((ulong)nodeIndex);
    }

    private static void WriteRangeNodeFrame(Stream stream, RangeNode node)
    {
        using var payload = new MemoryStream();
        MidgeDiskFormat.WriteUInt64(payload, node.Ordinal);
        MidgeDiskFormat.WriteUInt64(payload, node.Left);
        MidgeDiskFormat.WriteUInt64(payload, node.Right);
        WriteLength(payload, node.Start.Length);
        payload.Write(node.Start);
        WriteLength(payload, node.End.Length);
        payload.Write(node.End);
        WriteLength(payload, node.MaximumEnd.Length);
        payload.Write(node.MaximumEnd);
        WriteFrame(stream, payload.GetBuffer().AsSpan(0, checked((int)payload.Length)));
    }

    private static void WriteFrame(Stream stream, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MidgeDiskFormat.WalMaximumRecordBytes)
        {
            throw PantsException.ResourceLimit("A transaction spill frame exceeds the 64 MiB limit.");
        }

        MidgeDiskFormat.WriteUInt32(stream, checked((uint)payload.Length));
        MidgeDiskFormat.WriteUInt32(stream, MidgeDiskFormat.Crc32C(payload));
        stream.Write(payload);
    }

    private static byte[] ReadFrame(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        ReadExactly(stream, header, "Transaction spill frame header is truncated.");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (length > MidgeDiskFormat.WalMaximumRecordBytes)
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "Transaction spill frame exceeds the limit.");
        }

        byte[] payload = GC.AllocateUninitializedArray<byte>(checked((int)length));
        ReadExactly(stream, payload, "Transaction spill frame is truncated.");
        if (MidgeDiskFormat.Crc32C(payload) != expectedChecksum)
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "Transaction spill frame checksum does not match.");
        }

        return payload;
    }

    private static byte[] ReadField(ReadOnlySpan<byte> payload, ref int cursor)
    {
        if (cursor > payload.Length - sizeof(uint))
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "Transaction spill field length is truncated.");
        }

        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload[cursor..]));
        cursor += sizeof(uint);
        if (length < 0 || cursor > payload.Length - length)
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "Transaction spill field is truncated.");
        }

        byte[] value = payload.Slice(cursor, length).ToArray();
        cursor += length;
        return value;
    }

    private static void ReadExactly(Stream stream, Span<byte> destination, string message)
    {
        if (!MidgeDiskFormat.ReadExactly(stream, destination))
        {
            throw PantsException.Create(PantsErrorCode.Corruption, message);
        }
    }

    private static void WriteLength(Stream stream, int length) =>
        MidgeDiskFormat.WriteUInt32(stream, checked((uint)length));

    private static void DeleteIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            DeleteIfPresent(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record SpillRun(string Path, string RangePath, int RecordCount);

    private sealed record RangeNode(
        ulong Ordinal,
        ulong Left,
        ulong Right,
        byte[] Start,
        byte[] End,
        byte[] MaximumEnd);
}
