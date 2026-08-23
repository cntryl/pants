using System.Buffers.Binary;

namespace Cntryl.Pants;

internal sealed class TransactionSpillStore : IDisposable
{
    const int HeaderLength = 48;
    const int SparseIndexStride = 16;
    const int RangeHeaderLength = 32;
    const int RangeTableEntryLength = 12;
    const ulong NoRangeChild = ulong.MaxValue;

    static readonly Lock DirectoryMutationGate = new();

    static ReadOnlySpan<byte> RunMagic => "MDGTXN01"u8;

    static ReadOnlySpan<byte> RangeMagic => "MDGRNG01"u8;

    readonly string _directory;
    readonly ulong _transactionId;
    readonly ColumnFamilyIdentity _family;
    readonly List<TransactionSpillRun> _runs = [];
    readonly Lock _lifetimeGate = new();
    int _activeReadViews;
    bool _disposeRequested;
    int _disposed;

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

    internal static ulong RunHeaderLength => HeaderLength;

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
            _runs.Add(new TransactionSpillRun(runPath, rangePath, sorted.Length));
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

    public TransactionIntentReadView CreateReadView(
        IReadOnlyList<TransactionIntentOperation> residentOperations)
    {
        ArgumentNullException.ThrowIfNull(residentOperations);
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            _activeReadViews = checked(_activeReadViews + 1);
            try
            {
                return new TransactionIntentReadView(this, _runs.ToArray(), residentOperations);
            }
            catch
            {
                _activeReadViews--;
                throw;
            }
        }
    }

    internal IEnumerator<byte[]> CreateKeyCursor(
        TransactionSpillRun run,
        byte[]? startInclusive,
        byte[]? endExclusive,
        PantsScanDirection direction) =>
        new TransactionSpillRunKeyCursor(
            this,
            run,
            startInclusive,
            endExclusive,
            direction);

    internal void LookupLatest(
        IReadOnlyList<TransactionSpillRun> runs,
        ReadOnlySpan<byte> key,
        ref TransactionIntentLookup? latest)
    {
        foreach (var run in runs)
        {
            LookupRunKey(run, ulong.MaxValue, key, ref latest);
        }
    }

    internal TransactionIntentLookup? LatestBefore(ulong ordinal, ReadOnlySpan<byte> key)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        TransactionIntentLookup? latest = null;
        foreach (var run in _runs)
        {
            LookupRunKey(run, ordinal, key, ref latest);
        }

        return latest;
    }

    public void ForEach(Action<TransactionIntentOperation> visitor)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(visitor);
        foreach (var run in _runs)
        {
            ReadRun(run, visitor);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var delete = false;
        lock (_lifetimeGate)
        {
            _disposeRequested = true;
            delete = _activeReadViews == 0;
        }

        if (delete)
        {
            DeleteRuns();
        }
    }

    internal void ReleaseReadView()
    {
        var delete = false;
        lock (_lifetimeGate)
        {
            if (_activeReadViews <= 0)
            {
                throw new PantsInternalException("Transaction spill read-view accounting underflowed.");
            }

            _activeReadViews--;
            delete = _disposeRequested && _activeReadViews == 0;
        }

        if (delete)
        {
            DeleteRuns();
        }
    }

    void DeleteRuns()
    {
        foreach (var run in _runs)
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

    internal TransactionSpillRunHeader ReadRunHeader(
        Stream stream,
        TransactionSpillRun run)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        stream.Position = 0;
        ReadExactly(stream, header, "Transaction spill header is truncated.");
        if (!header[..8].SequenceEqual(RunMagic) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != 2 ||
            MidgeDiskFormat.Crc32C(header[..44]) !=
            BinaryPrimitives.ReadUInt32LittleEndian(header[44..]))
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill header is invalid.");
        }

        var recordCount = BinaryPrimitives.ReadUInt64LittleEndian(header[12..]);
        var ordinalTableOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[20..]);
        var sparseIndexOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[28..]);
        var sparseCount = BinaryPrimitives.ReadUInt64LittleEndian(header[36..]);
        var fileLength = checked((ulong)stream.Length);
        var expectedSparseCount = checked((recordCount + SparseIndexStride - 1) / SparseIndexStride);
        if (recordCount != checked((ulong)run.RecordCount) ||
            recordCount > int.MaxValue ||
            sparseCount > int.MaxValue ||
            ordinalTableOffset < HeaderLength ||
            ordinalTableOffset >= fileLength ||
            sparseIndexOffset <= ordinalTableOffset ||
            sparseIndexOffset > fileLength ||
            sparseCount != expectedSparseCount)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill metadata is inconsistent.");
        }

        return new TransactionSpillRunHeader(
            checked((int)recordCount),
            ordinalTableOffset,
            sparseIndexOffset,
            checked((int)sparseCount),
            fileLength);
    }

    internal static ulong FindSparseStart(
        Stream stream,
        TransactionSpillRunHeader header,
        byte[]? target)
    {
        var cursor = header.SparseIndexOffset;
        byte[]? previousKey = null;
        ulong? previousOperationOffset = null;
        var candidateOffset = (ulong)HeaderLength;
        var selectedOffset = (ulong)HeaderLength;
        var selected = target is null;
        for (var index = 0; index < header.SparseCount; index++)
        {
            stream.Position = checked((long)cursor);
            var payload = ReadFrame(stream);
            cursor = checked((ulong)stream.Position);
            var (key, operationOffset) = ParseSparseEntry(
                payload,
                header.OrdinalTableOffset,
                previousKey,
                previousOperationOffset,
                index);
            if (!selected && target is not null)
            {
                if (ByteArrayComparer.Instance.Compare(key, target) >= 0)
                {
                    selectedOffset = candidateOffset;
                    selected = true;
                }
                else
                {
                    candidateOffset = operationOffset;
                }
            }

            previousKey = key;
            previousOperationOffset = operationOffset;
        }

        if (!selected)
        {
            selectedOffset = candidateOffset;
        }

        if (cursor != header.FileLength)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill sparse index has an invalid extent.");
        }

        return selectedOffset;
    }

    internal static IReadOnlyList<ulong> ReadSparseOffsets(
        Stream stream,
        TransactionSpillRunHeader header)
    {
        var offsets = new List<ulong>(header.SparseCount);
        var cursor = header.SparseIndexOffset;
        byte[]? previousKey = null;
        ulong? previousOffset = null;
        for (var index = 0; index < header.SparseCount; index++)
        {
            stream.Position = checked((long)cursor);
            var payload = ReadFrame(stream);
            cursor = checked((ulong)stream.Position);
            var (key, operationOffset) = ParseSparseEntry(
                payload,
                header.OrdinalTableOffset,
                previousKey,
                previousOffset,
                index);
            offsets.Add(operationOffset);
            previousKey = key;
            previousOffset = operationOffset;
        }

        if (cursor != header.FileLength)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill sparse index has an invalid extent.");
        }

        return offsets;
    }

    static (byte[] Key, ulong Offset) ParseSparseEntry(
        byte[] payload,
        ulong ordinalTableOffset,
        byte[]? previousKey,
        ulong? previousOffset,
        int index)
    {
        if (payload.Length < 12)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill sparse index entry is truncated.");
        }

        var keyLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload));
        var keyEnd = checked(sizeof(uint) + keyLength);
        if (keyEnd > payload.Length || checked(keyEnd + sizeof(ulong)) != payload.Length)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill sparse index entry has an invalid length.");
        }

        var key = payload.AsSpan(sizeof(uint), keyLength).ToArray();
        if (previousKey is not null && ByteArrayComparer.Instance.Compare(previousKey, key) > 0)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill sparse index is not sorted.");
        }

        var operationOffset = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(keyEnd));
        if (operationOffset < HeaderLength || operationOffset >= ordinalTableOffset)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill sparse operation offset is out of bounds.");
        }

        if (previousOffset is { } priorOffset && operationOffset <= priorOffset)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill sparse operation offsets are not increasing.");
        }

        if (index == 0 && operationOffset != HeaderLength)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill sparse index does not cover the first operation.");
        }

        return (key, operationOffset);
    }

    internal static Exception MapReadException(Exception exception) => exception switch
    {
        PantsException => exception,
        OverflowException or ArgumentOutOfRangeException => PantsException.Create(
            PantsErrorCode.Corruption,
            "Transaction spill metadata exceeds supported bounds.",
            exception),
        _ => PantsException.Create(
            PantsErrorCode.Io,
            "A transaction spill run could not be read.",
            exception)
    };

    void LookupRunKey(
        TransactionSpillRun run,
        ulong ordinalCeiling,
        ReadOnlySpan<byte> key,
        ref TransactionIntentLookup? latest)
    {
        try
        {
            using var stream = new FileStream(
                run.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1_024,
                FileOptions.RandomAccess);
            var header = ReadRunHeader(stream, run);
            var keyCopy = key.ToArray();
            var cursor = FindSparseStart(stream, header, keyCopy);
            while (cursor < header.OrdinalTableOffset)
            {
                var recordOffset = cursor;
                stream.Position = checked((long)recordOffset);
                var (candidateOrdinal, candidateKey, kind) = ReadOperationPrimaryKeyFrame(stream);
                var nextCursor = checked((ulong)stream.Position);
                if (nextCursor > header.OrdinalTableOffset || nextCursor <= cursor)
                {
                    throw PantsException.Create(
                        PantsErrorCode.Corruption,
                        "Transaction spill data frame overlaps its ordinal table.");
                }

                cursor = nextCursor;
                var comparison = ByteArrayComparer.Instance.Compare(candidateKey, keyCopy);
                if (comparison > 0)
                {
                    break;
                }

                if (comparison == 0 &&
                    kind != CommitOperationKind.DeleteRange &&
                    candidateOrdinal < ordinalCeiling)
                {
                    stream.Position = checked((long)recordOffset);
                    var operation = ReadOperationFrame(stream);
                    if (operation.Ordinal != candidateOrdinal ||
                        checked((ulong)stream.Position) != nextCursor)
                    {
                        throw PantsException.Create(
                            PantsErrorCode.Corruption,
                            "Transaction spill point lookup changed frame boundaries.");
                    }

                    TransactionIntentLookup.Consider(ref latest, operation, key);
                }
            }

            LookupRangeIndex(run.RangePath, ordinalCeiling, key, ref latest);
        }
        catch (Exception exception)
        {
            throw MapReadException(exception);
        }
    }

    static void LookupRangeIndex(
        string path,
        ulong ordinalCeiling,
        ReadOnlySpan<byte> key,
        ref TransactionIntentLookup? latest)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1_024,
            FileOptions.RandomAccess);
        Span<byte> header = stackalloc byte[RangeHeaderLength];
        ReadExactly(stream, header, "Transaction range index header is truncated.");
        if (!header[..8].SequenceEqual(RangeMagic) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != 1 ||
            MidgeDiskFormat.Crc32C(header[..28]) != BinaryPrimitives.ReadUInt32LittleEndian(header[28..]))
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction range index header is invalid.");
        }

        var nodeCount = BinaryPrimitives.ReadUInt64LittleEndian(header[12..]);
        var nodeSectionOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[20..]);
        var streamLength = checked((ulong)stream.Length);
        if (nodeCount > int.MaxValue ||
            nodeSectionOffset != checked((ulong)RangeHeaderLength + (nodeCount * RangeTableEntryLength)) ||
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

        LookupRangeSubtree(
            stream,
            nodeIndex: 0,
            loaded: null,
            remainingNodes: nodeCount,
            nodeCount,
            nodeSectionOffset,
            streamLength,
            ordinalCeiling,
            key,
            ref latest);
    }

    static void LookupRangeSubtree(
        Stream stream,
        ulong nodeIndex,
        TransactionSpillRangeNode? loaded,
        ulong remainingNodes,
        ulong nodeCount,
        ulong nodeSectionOffset,
        ulong streamLength,
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

        var node = loaded ?? ReadRangeNode(
            stream,
            nodeIndex,
            nodeCount,
            nodeSectionOffset,
            streamLength);
        if (node.Left != NoRangeChild)
        {
            var left = ReadRangeNode(
                stream,
                node.Left,
                nodeCount,
                nodeSectionOffset,
                streamLength);
            if (key.SequenceCompareTo(left.MaximumEnd) < 0)
            {
                LookupRangeSubtree(
                    stream,
                    node.Left,
                    left,
                    remainingNodes - 1,
                    nodeCount,
                    nodeSectionOffset,
                    streamLength,
                    ordinalCeiling,
                    key,
                    ref latest);
            }
        }

        if (node.Ordinal < ordinalCeiling &&
            node.Start.AsSpan().SequenceCompareTo(key) <= 0 &&
            key.SequenceCompareTo(node.End) < 0 &&
            (latest is null || node.Ordinal > latest.Ordinal))
        {
            latest = new TransactionIntentLookup(node.Ordinal, null, IsDeleted: true);
        }

        if (node.Right != NoRangeChild && node.Start.AsSpan().SequenceCompareTo(key) <= 0)
        {
            LookupRangeSubtree(
                stream,
                node.Right,
                loaded: null,
                remainingNodes - 1,
                nodeCount,
                nodeSectionOffset,
                streamLength,
                ordinalCeiling,
                key,
                ref latest);
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

    void ReadRun(TransactionSpillRun run, Action<TransactionIntentOperation> visitor)
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

            var recordCount = BinaryPrimitives.ReadUInt64LittleEndian(header[12..]);
            var ordinalTableOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[20..]);
            var sparseIndexOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[28..]);
            var sparseCount = BinaryPrimitives.ReadUInt64LittleEndian(header[36..]);
            var streamLength = checked((ulong)stream.Length);
            var expectedSparseCount = checked((recordCount + SparseIndexStride - 1) / SparseIndexStride);
            if (recordCount != checked((ulong)run.RecordCount) ||
                ordinalTableOffset < HeaderLength ||
                ordinalTableOffset >= streamLength ||
                sparseIndexOffset <= ordinalTableOffset ||
                sparseIndexOffset > streamLength ||
                sparseCount != expectedSparseCount)
            {
                throw PantsException.Create(PantsErrorCode.Corruption, "Transaction spill metadata is inconsistent.");
            }

            stream.Position = checked((long)ordinalTableOffset);
            ulong? previousOrdinal = null;
            for (var index = 0; index < run.RecordCount; index++)
            {
                var entry = ReadFrame(stream);
                if (entry.Length != 16)
                {
                    throw PantsException.Create(
                        PantsErrorCode.Corruption,
                        "Transaction spill ordinal entry has an invalid length.");
                }

                var nextTablePosition = stream.Position;
                var ordinal = BinaryPrimitives.ReadUInt64LittleEndian(entry);
                var offset = BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(8));
                if (offset < HeaderLength || offset >= ordinalTableOffset)
                {
                    throw PantsException.Create(
                        PantsErrorCode.Corruption,
                        "Transaction spill operation offset is out of bounds.");
                }

                if (previousOrdinal is { } previous && ordinal <= previous)
                {
                    throw PantsException.Create(
                        PantsErrorCode.Corruption,
                        "Transaction spill ordinal entries are not increasing.");
                }

                stream.Position = checked((long)offset);
                var operation = ReadOperationFrame(stream, ordinal);
                if (checked((ulong)stream.Position) > ordinalTableOffset)
                {
                    throw PantsException.Create(
                        PantsErrorCode.Corruption,
                        "Transaction spill operation exceeds its data section.");
                }

                visitor(operation);
                previousOrdinal = ordinal;
                stream.Position = nextTablePosition;
            }

            if (checked((ulong)stream.Position) != sparseIndexOffset)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "Transaction spill ordinal table has an invalid extent.");
            }

            ValidateSparseIndex(
                stream,
                ordinalTableOffset,
                sparseIndexOffset,
                checked((int)sparseCount),
                run.RecordCount);
            ValidateRangeFile(
                run.RangePath,
                stream,
                ordinalTableOffset,
                run.RecordCount);
        }
        catch (PantsException)
        {
            throw;
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill metadata exceeds supported bounds.",
                exception);
        }
        catch (Exception exception)
        {
            throw PantsException.Create(
                PantsErrorCode.Io,
                "A transaction spill run could not be read.",
                exception);
        }
    }

    static void WriteOperationFrame(Stream stream, TransactionIntentOperation operation)
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

    internal TransactionIntentOperation ReadOperationFrame(
        Stream stream,
        ulong? expectedOrdinal = null)
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
        if ((expectedOrdinal is { } expected && ordinal != expected) ||
            familyId != _family.Id || ttlPresent > 1 ||
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

        var maximumTimeToLiveSeconds = checked((ulong)(TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond));
        if (ttlPresent != 0 && ttlSeconds > maximumTimeToLiveSeconds)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill TTL exceeds the supported range.");
        }

        TimeSpan? ttl = ttlPresent == 0
            ? null
            : TimeSpan.FromSeconds((long)ttlSeconds);
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

    internal (ulong Ordinal, byte[] Key, CommitOperationKind Kind) ReadOperationPrimaryKeyFrame(Stream stream)
    {
        Span<byte> frameHeader = stackalloc byte[8];
        ReadExactly(stream, frameHeader, "Transaction spill frame header is truncated.");
        var payloadLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(frameHeader));
        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(frameHeader[4..]);
        if (payloadLength > MidgeDiskFormat.WalMaximumRecordBytes)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill frame exceeds the limit.");
        }

        if (payloadLength < 30)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill operation is truncated.");
        }

        var checksum = 0U;
        Span<byte> fixedFields = stackalloc byte[22];
        ReadCrcExactly(
            stream,
            fixedFields,
            ref checksum,
            "Transaction spill operation metadata is truncated.");
        var ordinal = BinaryPrimitives.ReadUInt64LittleEndian(fixedFields);
        var tag = fixedFields[8];
        var familyId = BinaryPrimitives.ReadUInt32LittleEndian(fixedFields[9..]);
        var ttlPresent = fixedFields[13];
        var ttlSeconds = BinaryPrimitives.ReadUInt64LittleEndian(fixedFields[14..]);
        if (tag > 3 || familyId != _family.Id || ttlPresent > 1 ||
            (ttlPresent == 0 && ttlSeconds != 0) ||
            (tag >= 2 && ttlPresent != 0))
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill operation metadata is invalid.");
        }

        var maximumTimeToLiveSeconds = checked(
            (ulong)(TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond));
        if (ttlPresent != 0 && ttlSeconds > maximumTimeToLiveSeconds)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill TTL exceeds the supported range.");
        }

        Span<byte> fieldLength = stackalloc byte[sizeof(uint)];
        ReadCrcExactly(
            stream,
            fieldLength,
            ref checksum,
            "Transaction spill key length is truncated.");
        var keyLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(fieldLength));
        var consumed = 22 + sizeof(uint);
        if (keyLength > payloadLength - consumed - sizeof(uint))
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill key length exceeds its frame.");
        }

        var key = GC.AllocateUninitializedArray<byte>(keyLength);
        ReadCrcExactly(
            stream,
            key,
            ref checksum,
            "Transaction spill key is truncated.");
        consumed = checked(consumed + keyLength);
        ReadCrcExactly(
            stream,
            fieldLength,
            ref checksum,
            "Transaction spill value length is truncated.");
        consumed = checked(consumed + sizeof(uint));
        var secondLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(fieldLength));
        if (secondLength != payloadLength - consumed ||
            (tag == 2 && secondLength != 0))
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill operation payload is invalid.");
        }

        Span<byte> scratch = stackalloc byte[8 * 1_024];
        var remaining = secondLength;
        while (remaining != 0)
        {
            var chunkLength = Math.Min(remaining, scratch.Length);
            ReadCrcExactly(
                stream,
                scratch[..chunkLength],
                ref checksum,
                "Transaction spill value is truncated.");
            remaining -= chunkLength;
        }

        if (checksum != expectedChecksum)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill frame checksum does not match.");
        }

        return (ordinal, key, tag switch
        {
            0 or 1 => CommitOperationKind.Put,
            2 => CommitOperationKind.Delete,
            3 => CommitOperationKind.DeleteRange,
            _ => throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill operation tag is invalid.")
        });
    }

    static void ReadCrcExactly(
        Stream stream,
        Span<byte> destination,
        ref uint checksum,
        string message)
    {
        ReadExactly(stream, destination, message);
        checksum = MidgeDiskFormat.Crc32CAppend(checksum, destination);
    }

    static void WriteRangeFile(string path, IReadOnlyList<TransactionIntentOperation> operations)
    {
        var nodes = BuildRangeNodes(operations);
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

    void ValidateSparseIndex(
        Stream stream,
        ulong ordinalTableOffset,
        ulong sparseIndexOffset,
        int sparseCount,
        int recordCount)
    {
        var sparseCursor = sparseIndexOffset;
        var operationCursor = (ulong)HeaderLength;
        byte[]? previousKey = null;
        ulong? previousOffset = null;
        for (var index = 0; index < sparseCount; index++)
        {
            stream.Position = checked((long)sparseCursor);
            var payload = ReadFrame(stream);
            sparseCursor = checked((ulong)stream.Position);
            var (key, operationOffset) = ParseSparseEntry(
                payload,
                ordinalTableOffset,
                previousKey,
                previousOffset,
                index);
            if (operationOffset != operationCursor)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "Transaction spill sparse index does not match its operation stride.");
            }

            var operationsInStride = Math.Min(
                SparseIndexStride,
                checked(recordCount - index * SparseIndexStride));
            byte[]? indexedOperationKey = null;
            for (var operationIndex = 0; operationIndex < operationsInStride; operationIndex++)
            {
                stream.Position = checked((long)operationCursor);
                var (_, operationKey, _) = ReadOperationPrimaryKeyFrame(stream);
                if (operationIndex == 0)
                {
                    indexedOperationKey = operationKey;
                }

                operationCursor = checked((ulong)stream.Position);
                if (operationCursor > ordinalTableOffset)
                {
                    throw PantsException.Create(
                        PantsErrorCode.Corruption,
                        "Transaction spill operation exceeds its data section.");
                }
            }

            if (indexedOperationKey is null || !key.AsSpan().SequenceEqual(indexedOperationKey))
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "Transaction spill sparse key does not match its indexed operation.");
            }

            previousKey = key;
            previousOffset = operationOffset;
        }

        if (operationCursor != ordinalTableOffset || sparseCursor != checked((ulong)stream.Length))
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill sparse index has an invalid extent.");
        }
    }

    void ValidateRangeFile(
        string path,
        Stream runStream,
        ulong ordinalTableOffset,
        int recordCount)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.RandomAccess);
        Span<byte> header = stackalloc byte[RangeHeaderLength];
        ReadExactly(stream, header, "Transaction range index header is truncated.");
        if (!header[..8].SequenceEqual(RangeMagic) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != 1 ||
            MidgeDiskFormat.Crc32C(header[..28]) != BinaryPrimitives.ReadUInt32LittleEndian(header[28..]))
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "Transaction range index header is invalid.");
        }

        var nodeCount = BinaryPrimitives.ReadUInt64LittleEndian(header[12..]);
        var nodeSectionOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[20..]);
        var streamLength = checked((ulong)stream.Length);
        if (nodeCount > int.MaxValue ||
            nodeCount > (streamLength - Math.Min(streamLength, (ulong)RangeHeaderLength)) /
            RangeTableEntryLength)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction range node count exceeds the file bounds.");
        }

        var expectedNodeSectionOffset =
            (ulong)RangeHeaderLength + (nodeCount * RangeTableEntryLength);
        if (nodeSectionOffset != expectedNodeSectionOffset || nodeSectionOffset > streamLength)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction range node section offset is invalid.");
        }

        ulong? previousNodeEnd = null;
        Span<byte> entry = stackalloc byte[RangeTableEntryLength];
        for (var index = 0UL; index < nodeCount; index++)
        {
            stream.Position = checked((long)((ulong)RangeHeaderLength + (index * RangeTableEntryLength)));
            ReadExactly(stream, entry, "Transaction range offset entry is truncated.");
            if (MidgeDiskFormat.Crc32C(entry[..8]) != BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]))
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
            var node = ReadRangeNodeFrame(stream, nodeCount);
            ValidateRangeNodeOperation(
                runStream,
                ordinalTableOffset,
                recordCount,
                node);
            previousNodeEnd = checked((ulong)stream.Position);
        }

        if ((previousNodeEnd ?? nodeSectionOffset) != streamLength)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction range node section has an invalid extent.");
        }

        ValidateRangeGraph(stream, checked((int)nodeCount), nodeSectionOffset, streamLength);
    }

    void ValidateRangeNodeOperation(
        Stream runStream,
        ulong ordinalTableOffset,
        int recordCount,
        TransactionSpillRangeNode node)
    {
        var lower = 0;
        var upper = recordCount - 1;
        while (lower <= upper)
        {
            var middle = lower + ((upper - lower) / 2);
            var entryOffset = checked(
                ordinalTableOffset + (ulong)middle * (2 * sizeof(uint) + 2 * sizeof(ulong)));
            runStream.Position = checked((long)entryOffset);
            var entry = ReadFrame(runStream);
            if (entry.Length != 2 * sizeof(ulong))
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "Transaction spill ordinal entry has an invalid length.");
            }

            var ordinal = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            if (ordinal < node.Ordinal)
            {
                lower = middle + 1;
                continue;
            }

            if (ordinal > node.Ordinal)
            {
                upper = middle - 1;
                continue;
            }

            var operationOffset = BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(sizeof(ulong)));
            runStream.Position = checked((long)operationOffset);
            var operation = ReadOperationFrame(runStream, node.Ordinal);
            if (operation.Kind != CommitOperationKind.DeleteRange ||
                !operation.Key.AsSpan().SequenceEqual(node.Start) ||
                operation.EndExclusive is null ||
                !operation.EndExclusive.AsSpan().SequenceEqual(node.End))
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "Transaction range node does not match its delete-range operation.");
            }

            return;
        }

        throw PantsException.Create(
            PantsErrorCode.Corruption,
            "Transaction range node does not reference a spill operation.");
    }

    static void ValidateRangeGraph(
        Stream stream,
        int nodeCount,
        ulong nodeSectionOffset,
        ulong streamLength)
    {
        if (nodeCount == 0)
        {
            return;
        }

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
            var node = ReadRangeNode(
                stream,
                nodeIndex,
                checked((ulong)nodeCount),
                nodeSectionOffset,
                streamLength);
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
            ValidateChild(node.Left, isLeft: true);
            ValidateChild(node.Right, isLeft: false);
            if (!expectedMaximumEnd.AsSpan().SequenceEqual(node.MaximumEnd))
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "Transaction range subtree maximum is invalid.");
            }

            void ValidateChild(ulong childIndex, bool isLeft)
            {
                if (childIndex == NoRangeChild)
                {
                    return;
                }

                var child = ReadRangeNode(
                    stream,
                    childIndex,
                    checked((ulong)nodeCount),
                    nodeSectionOffset,
                    streamLength);
                var ordering = ByteArrayComparer.Instance.Compare(child.Start, node.Start);
                if ((isLeft && ordering > 0) || (!isLeft && ordering < 0))
                {
                    throw PantsException.Create(
                        PantsErrorCode.Corruption,
                        "Transaction range index ordering is invalid.");
                }

                if (ByteArrayComparer.Instance.Compare(child.MaximumEnd, expectedMaximumEnd) > 0)
                {
                    expectedMaximumEnd = child.MaximumEnd;
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

    static TransactionSpillRangeNode ReadRangeNode(
        Stream stream,
        ulong nodeIndex,
        ulong nodeCount,
        ulong nodeSectionOffset,
        ulong streamLength)
    {
        if (nodeIndex >= nodeCount)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction range child is out of bounds.");
        }

        stream.Position = checked((long)((ulong)RangeHeaderLength + (nodeIndex * RangeTableEntryLength)));
        Span<byte> entry = stackalloc byte[RangeTableEntryLength];
        ReadExactly(stream, entry, "Transaction range offset entry is truncated.");
        if (MidgeDiskFormat.Crc32C(entry[..8]) != BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]))
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
        return ReadRangeNodeFrame(stream, nodeCount);
    }

    static TransactionSpillRangeNode ReadRangeNodeFrame(Stream stream, ulong nodeCount)
    {
        var payload = ReadFrame(stream);
        if (payload.Length < 36)
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "Transaction range node is truncated.");
        }

        var left = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(8));
        var right = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(16));
        if ((left != NoRangeChild && left >= nodeCount) ||
            (right != NoRangeChild && right >= nodeCount))
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction range child is out of bounds.");
        }

        var cursor = 24;
        var start = ReadField(payload, ref cursor);
        var end = ReadField(payload, ref cursor);
        var maximumEnd = ReadField(payload, ref cursor);
        if (cursor != payload.Length ||
            ByteArrayComparer.Instance.Compare(start, end) > 0 ||
            ByteArrayComparer.Instance.Compare(maximumEnd, end) < 0)
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
            maximumEnd);
    }

    static TransactionSpillRangeNode[] BuildRangeNodes(
        IReadOnlyList<TransactionIntentOperation> operations)
    {
        int[] operationIndexes = operations
            .Select(static (operation, index) => (operation, index))
            .Where(static item => item.operation.Kind == CommitOperationKind.DeleteRange)
            .Select(static item => item.index)
            .ToArray();
        var nodes = new List<TransactionSpillRangeNode>(operationIndexes.Length);
        BuildRangeSubtree(operationIndexes, operations, nodes);
        return nodes.ToArray();
    }

    static ulong? BuildRangeSubtree(
        ReadOnlySpan<int> operationIndexes,
        IReadOnlyList<TransactionIntentOperation> operations,
        List<TransactionSpillRangeNode> nodes)
    {
        if (operationIndexes.IsEmpty)
        {
            return null;
        }

        int middle = operationIndexes.Length / 2;
        TransactionIntentOperation operation = operations[operationIndexes[middle]];
        int nodeIndex = nodes.Count;
        var node = new TransactionSpillRangeNode(
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

    static void WriteRangeNodeFrame(Stream stream, TransactionSpillRangeNode node)
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

    static void WriteFrame(Stream stream, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MidgeDiskFormat.WalMaximumRecordBytes)
        {
            throw PantsException.ResourceLimit("A transaction spill frame exceeds the 64 MiB limit.");
        }

        MidgeDiskFormat.WriteUInt32(stream, checked((uint)payload.Length));
        MidgeDiskFormat.WriteUInt32(stream, MidgeDiskFormat.Crc32C(payload));
        stream.Write(payload);
    }

    static byte[] ReadFrame(Stream stream)
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

    static byte[] ReadField(ReadOnlySpan<byte> payload, ref int cursor)
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

    static int ReadFieldLength(ReadOnlySpan<byte> payload, ref int cursor)
    {
        if (cursor > payload.Length - sizeof(uint))
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Transaction spill field length is truncated.");
        }

        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload[cursor..]));
        cursor += sizeof(uint);
        return length;
    }

    static void ReadExactly(Stream stream, Span<byte> destination, string message)
    {
        if (!MidgeDiskFormat.ReadExactly(stream, destination))
        {
            throw PantsException.Create(PantsErrorCode.Corruption, message);
        }
    }

    static void WriteLength(Stream stream, int length) =>
        MidgeDiskFormat.WriteUInt32(stream, checked((uint)length));

    static void DeleteIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            DeleteIfPresent(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

}
