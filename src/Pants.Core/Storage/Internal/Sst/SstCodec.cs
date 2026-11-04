using System.Buffers.Binary;
using System.IO.Hashing;
using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants.Storage.Internal.Sst;

static class SstCodec
{
    const int TargetBlockSize = 64 * 1024;
    internal const ulong MaximumEncodedDataBlockBytes = DiskFormat.MaximumDecodedBlockBytes + 1024UL * 1024;
    const int RestartInterval = 16;
    // Include the entry object, array headers/alignment and its list slot before materializing.
    internal const int DecodedEntryOverheadBytes = 160;
    internal const long DecodedCollectionOverheadBytes = 96;
    const long DefaultMaximumDecodedEntryBytes = 16L * DiskFormat.MaximumDecodedBlockBytes;
    const uint ContinuedKeyFlag = 0x8000_0000;
    const int SingletonRangeBlockOverhead = sizeof(uint) + 2 * sizeof(uint) + sizeof(ulong);

    /// <summary>
    ///     Rejects a key/value pair that could not be encoded into an SST block.
    /// </summary>
    /// <remarks>
    ///     Measured against the worst case, where the entry sits first in its block and so stores
    ///     its key in full: prefix compression is a property of an entry's position, which flush and
    ///     compaction choose later. Admitting on the compressed size would let a write succeed that
    ///     no subsequent flush could write down.
    /// </remarks>
    public static void ValidateEntrySize(int keyLength, int valueLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keyLength);
        ArgumentOutOfRangeException.ThrowIfNegative(valueLength);

        var header = 1 + VarintLength((uint)keyLength) + VarintLength((uint)valueLength) +
                     sizeof(ulong) + 1 + sizeof(ulong) + 2 * sizeof(uint);
        var encoded = (long)header + keyLength + valueLength;
        if (encoded > DiskFormat.MaximumDecodedBlockBytes)
        {
            throw new PantsResourceLimitException(
                $"An entry with {keyLength} key bytes and {valueLength} value bytes encodes to " +
                $"{encoded} bytes, which exceeds the " +
                $"{DiskFormat.MaximumDecodedBlockBytes}-byte decoded block limit.");
        }
    }

    /// <summary>
    ///     Rejects a range tombstone whose bounds could not be encoded into a block.
    /// </summary>
    /// <remarks>
    ///     Each bound must be admissible as a point key, and the singleton range block - count, two
    ///     bound lengths, sequence and both bounds - must fit the decoded block limit.
    /// </remarks>
    public static void ValidateRangeTombstoneSize(int startLength, int endLength)
    {
        ValidateEntrySize(startLength, 0);
        ValidateEntrySize(endLength, 0);

        var encoded = (long)SingletonRangeBlockOverhead + startLength + endLength;
        if (encoded > DiskFormat.MaximumDecodedBlockBytes)
        {
            throw new PantsResourceLimitException(
                $"A range tombstone spanning {startLength} and {endLength} bound bytes encodes to " +
                $"{encoded} bytes, which exceeds the " +
                $"{DiskFormat.MaximumDecodedBlockBytes}-byte decoded block limit.");
        }
    }
    const ulong BloomSeedOne = 0x9E37_79B1_85EB_CA87;
    const ulong BloomSeedTwo = 0xC2B2_AE3D_27D4_EB4F;

    internal static void ValidateDataBlockSize(SstBlockHandle handle)
    {
        if (handle.Size > MaximumEncodedDataBlockBytes)
        {
            throw new PantsResourceLimitException("Encoded SST data block exceeds the bounded read admission limit.");
        }
    }

    public static byte[] Encode(
        IReadOnlyList<SstEntry> sourceEntries,
        IReadOnlyList<RangeTombstone> tombstones,
        PantsPerformanceGoal performanceGoal)
    {
        using var file = new MemoryStream();
        EncodeTo(file, sourceEntries, tombstones, performanceGoal);
        return file.ToArray();
    }

    public static void EncodeTo(
        Stream file,
        IReadOnlyList<SstEntry> sourceEntries,
        IReadOnlyList<RangeTombstone> tombstones,
        PantsPerformanceGoal performanceGoal)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.CanWrite || !file.CanSeek || file.Position != 0)
        {
            throw new ArgumentException(
                "SST output must be a writable, seekable stream positioned at zero.",
                nameof(file));
        }

        // Validate everything before the first byte lands so a rejected input leaves no
        // partial file behind for a caller to publish or clean up.
        foreach (var entry in sourceEntries)
        {
            ValidateEntrySize(entry.Key.Length, entry.Value?.Length ?? 0);
            ValidateEntryType(entry);
        }

        foreach (var tombstone in tombstones)
        {
            ValidateRangeTombstoneSize(tombstone.Start.Length, tombstone.End.Length);
        }

        var entries = sourceEntries
            .OrderBy(entry => entry.Key, ByteArrayComparer.Instance)
            .ThenByDescending(entry => entry.Sequence)
            .ToList();
        var index = new List<(byte[] FirstKey, SstBlockHandle Handle)>();
        var blockKeys = new List<IReadOnlyList<byte[]>>();
        var currentBlockKeys = new List<byte[]>();
        var keyProfiler = new KeyStructureProfiler();
        var restarts = new List<uint>();
        var entriesInBlock = 0;
        using var block = new MemoryStream();
        byte[] previousKey = [];
        byte[]? firstKey = null;
        foreach (var entry in entries)
        {
            keyProfiler.Add(entry.Key);
            var restart = entriesInBlock % RestartInterval == 0;
            var encoded = EncodeEntry(restart ? [] : previousKey, entry);
            if (block.Length > 0 &&
                block.Length + encoded.Length + (restarts.Count + (restart ? 1 : 0) + 1) * sizeof(uint) > TargetBlockSize)
            {
                AppendRestartDirectory(block, restarts);
                index.Add((firstKey!, AppendBlock(file, block.ToArray(), performanceGoal)));
                blockKeys.Add(currentBlockKeys.ToArray());
                currentBlockKeys.Clear();
                block.SetLength(0);
                restarts.Clear();
                entriesInBlock = 0;
                restart = true;
                previousKey = [];
                firstKey = null;
                encoded = EncodeEntry(previousKey, entry);
            }

            if (restart)
            {
                restarts.Add(checked((uint)block.Length) |
                             (entriesInBlock > 0 && previousKey.AsSpan().SequenceEqual(entry.Key)
                                 ? ContinuedKeyFlag
                                 : 0));
            }

            firstKey ??= entry.Key;
            block.Write(encoded);
            entriesInBlock++;
            currentBlockKeys.Add(entry.Key);
            previousKey = entry.Key;
        }

        if (block.Length > 0)
        {
            AppendRestartDirectory(block, restarts);
            index.Add((firstKey!, AppendBlock(file, block.ToArray(), performanceGoal)));
            blockKeys.Add(currentBlockKeys.ToArray());
        }

        // Bloom placement is writer policy. Readers also accept a present range block at offset zero.
        var blockBloomHandle = AppendBlock(file, EncodeBlockBlooms(blockKeys), performanceGoal);
        SstBlockHandle? rangeHandle = tombstones.Count == 0
            ? null
            : AppendBlock(file, EncodeRangeTombstones(tombstones), performanceGoal);
        var indexKind = SstIndexTuner.Decide(keyProfiler.Finish());
        SstBlockHandle? trieHandle = indexKind == SstIndexKind.Trie
            ? AppendBlock(
                file,
                TrieIndex.Encode(index.Select(static entry => entry.FirstKey).ToArray()),
                performanceGoal)
            : null;
        var metaHandle = AppendBlock(
            file,
            EncodeMetadata(entries, tombstones, rangeHandle, indexKind),
            performanceGoal);
        var indexHandle = AppendBlock(file, EncodeIndex(index), performanceGoal);
        file.Write(EncodeFooter(metaHandle, indexHandle, trieHandle, blockBloomHandle));
    }

    public static SstContents Decode(byte[] bytes)
    {
        if (bytes.Length < DiskFormat.SstFooterSize)
        {
            if (bytes.Length >= sizeof(ulong) &&
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - sizeof(ulong))) ==
                DiskFormat.SstFooterMagic)
            {
                throw new PantsCompatibilityException("SST matches a legacy footer magic.");
            }

            throw new PantsCorruptionException("SST is shorter than its V5 footer.");
        }

        var footerOffset = bytes.Length - DiskFormat.SstFooterSize;
        var footer = bytes.AsSpan(footerOffset);
        ValidateFooter(footer);

        var metaHandle = ReadHandle(footer, 0);
        var indexHandle = ReadHandle(footer, 16);
        var trieHandle = ReadOptionalHandle(footer, 32, "trie");
        var bloomHandle = ReadOptionalHandle(footer, 48, "block bloom");
        var meta = ReadBlock(bytes, metaHandle);
        var index = DecodeIndex(ReadBlock(bytes, indexHandle));
        var metadata = DecodeMetadata(meta);
        var tombstones = metadata.RangeHandle is null
            ? []
            : DecodeRangeTombstones(ReadBlock(bytes, metadata.RangeHandle.Value));
        if (bloomHandle is { } bloom)
        {
            ValidateBlockBlooms(ReadBlock(bytes, bloom), index.Count);
        }

        _ = DecodeTrieIndex(bytes, metadata.IndexKind, trieHandle, index);

        ValidateBlockCoverage(
            footerOffset,
            metaHandle,
            indexHandle,
            trieHandle,
            bloomHandle,
            metadata.RangeHandle,
            index);
        var entries = new List<SstEntry>();
        foreach (var (firstKey, handle) in index)
        {
            ValidateDataBlockSize(handle);
            var firstEntryIndex = entries.Count;
            DecodeEntries(ReadBlock(bytes, handle), entries);
            if (entries.Count == firstEntryIndex ||
                !entries[firstEntryIndex].Key.AsSpan().SequenceEqual(firstKey))
            {
                throw new PantsCorruptionException("SST index first key does not match its data block.");
            }
        }

        ValidateEntryOrdering(entries);
        ValidateMetadataKeyRange(metadata, entries, tombstones);

        return new SstContents(entries, tombstones, index.Count);
    }

    internal static SstPointReadDecision GetPointReadDecision(
        byte[] bytes,
        ReadOnlySpan<byte> key)
    {
        if (bytes.Length < DiskFormat.SstFooterSize)
        {
            if (bytes.Length >= sizeof(ulong) &&
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - sizeof(ulong))) ==
                DiskFormat.SstFooterMagic)
            {
                throw new PantsCompatibilityException("SST matches a legacy footer magic.");
            }

            throw new PantsCorruptionException("SST is shorter than its V5 footer.");
        }

        ReadOnlySpan<byte> footer = bytes.AsSpan(bytes.Length - DiskFormat.SstFooterSize);
        ValidateFooter(footer);
        var index = DecodeIndex(
            ReadBlock(bytes, ReadHandle(footer, 16)));
        var metadata = DecodeMetadata(ReadBlock(bytes, ReadHandle(footer, 0)));
        var trieIndex = DecodeTrieIndex(
            bytes,
            metadata.IndexKind,
            ReadOptionalHandle(footer, 32, "trie"),
            index);
        var bloomHandle = ReadOptionalHandle(footer, 48, "block bloom");
        if (index.Count == 0)
        {
            return new SstPointReadDecision(0, 0, 0, false, -1, 0);
        }

        var trieCandidate = trieIndex?.FindFloorBlock(key) ?? -1;
        var candidate = trieCandidate >= 0 ? trieCandidate : FindFloorBlock(index, key);

        if (candidate < 0)
        {
            return new SstPointReadDecision(0, 0, 0, false, -1, 0);
        }

        var mightContain = bloomHandle is null || BloomMightContain(
            ReadBlock(bytes, bloomHandle.Value),
            candidate,
            key);
        var bloomChecks = bloomHandle is null ? 0 : 1;
        var candidateHandle = index[candidate].Handle;
        var blockSizeBytes = DecodeInt32(candidateHandle.Size, "block size");
        return mightContain
            ? new SstPointReadDecision(bloomChecks, 1, 1, false, candidate, blockSizeBytes)
            : new SstPointReadDecision(1, 1, 0, true, candidate, blockSizeBytes);
    }

    internal static SstIndexKind GetIndexKind(byte[] bytes)
    {
        if (bytes.Length < DiskFormat.SstFooterSize)
        {
            if (bytes.Length >= sizeof(ulong) &&
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - sizeof(ulong))) ==
                DiskFormat.SstFooterMagic)
            {
                throw new PantsCompatibilityException("SST matches a legacy footer magic.");
            }

            throw new PantsCorruptionException("SST is shorter than its V5 footer.");
        }

        ReadOnlySpan<byte> footer = bytes.AsSpan(bytes.Length - DiskFormat.SstFooterSize);
        ValidateFooter(footer);
        var metadata = DecodeMetadata(ReadBlock(bytes, ReadHandle(footer, 0)));
        var index = DecodeIndex(
            ReadBlock(bytes, ReadHandle(footer, 16)));
        _ = DecodeTrieIndex(
            bytes,
            metadata.IndexKind,
            ReadOptionalHandle(footer, 32, "trie"),
            index);
        return metadata.IndexKind;
    }

    internal static bool DataBlockContainsKey(byte[] block, ReadOnlySpan<byte> key)
    {
        var entries = new List<SstEntry>();
        DecodeEntries(block, entries, pointKey: key.ToArray());
        foreach (var entry in entries)
        {
            if (entry.Key.AsSpan().SequenceEqual(key))
            {
                return true;
            }
        }

        return false;
    }

    internal static IReadOnlyList<SstEntry> DecodeDataBlock(
        ReadOnlySpan<byte> block, long maximumDecodedEntryBytes = DefaultMaximumDecodedEntryBytes)
    {
        var entries = new List<SstEntry>();
        DecodeEntries(block, entries, maximumDecodedEntryBytes);
        return entries;
    }

    // Retain the block boundaries for the caller's cross-block validation and every version of
    // the requested key. Other values remain borrowed while the shared decoder validates them.
    internal static IReadOnlyList<SstEntry> DecodePointDataBlock(ReadOnlyMemory<byte> block, byte[] key)
    {
        var entries = new List<SstEntry>();
        DecodeEntries(block.Span, entries, pointKey: key);
        return entries;
    }

    /// <summary>
    ///     Builds the same <see cref="SstContents" /> shape as <see cref="Decode" /> (used by
    ///     compaction's input-reading step), but walks <paramref name="reader" /> one block at a time
    ///     via <see cref="SstBlockIterator" /> instead of requiring the caller to have already read
    ///     and decoded the whole file — the caller never holds more than one input SST's worth of
    ///     decoded blocks (plus the entries accumulated so far) at once. Range tombstones come
    ///     straight from <see cref="SstReader.RangeTombstones" />, already loaded (small/bounded) by
    ///     <see cref="SstReader.Open" />.
    /// </summary>
    internal static SstContents DecodeViaBlockIterator(SstReader reader)
    {
        var entries = new List<SstEntry>();
        using var iterator = SstBlockIterator.Create(reader, PantsScanDirection.Forward);
        while (iterator.MoveNext())
        {
            entries.Add(iterator.Current);
        }

        return new SstContents(entries, reader.RangeTombstones, reader.DataBlockCount);
    }

    static void ValidateEntryType(SstEntry entry)
    {
        if (entry.EntryType == 3)
        {
            throw new PantsCompatibilityException("SST Merge entries have no supported resolution semantics.");
        }

        if (entry.EntryType > 2 || entry.IsDelete != (entry.EntryType == 2) ||
            (entry.IsDelete && entry.Value is { Length: > 0 }))
        {
            throw new PantsCorruptionException("SST data entry metadata is invalid or a delete carries a value.");
        }
    }

    static void AppendRestartDirectory(Stream block, List<uint> restarts)
    {
        foreach (var restart in restarts)
        {
            DiskFormat.WriteUInt32(block, restart);
        }

        DiskFormat.WriteUInt32(block, checked((uint)restarts.Count));
    }

    static byte[] EncodeEntry(byte[] previousKey, SstEntry entry)
    {
        var shared = 0;
        var maxShared = Math.Min(Math.Min(previousKey.Length, entry.Key.Length), ushort.MaxValue);
        while (shared < maxShared && previousKey[shared] == entry.Key[shared])
        {
            shared++;
        }

        var keyLength = entry.Key.Length - shared;
        var value = entry.Value ?? [];
        using var output = new MemoryStream();
        WriteVarint(output, (uint)shared);
        WriteVarint(output, checked((uint)keyLength));
        WriteVarint(output, checked((uint)value.Length));
        DiskFormat.WriteUInt64(output, entry.Sequence);
        output.WriteByte((byte)(entry.EntryType | (entry.Expiration.HasValue ? 0x80 : 0)));
        if (entry.Expiration is { } expiration)
        {
            DiskFormat.WriteUInt64(output, expiration);
        }

        output.Write(entry.Key, shared, keyLength);
        output.Write(value);
        return output.ToArray();
    }

    static int VarintLength(uint value)
    {
        var length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }

        return length;
    }

    static void WriteVarint(Stream output, uint value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }

        output.WriteByte((byte)value);
    }

    static uint ReadVarint(ReadOnlySpan<byte> bytes, ref int cursor)
    {
        uint value = 0;
        for (var index = 0; index < 5; index++)
        {
            if (cursor >= bytes.Length)
            {
                throw new PantsCorruptionException("SST varint is truncated.");
            }

            var current = bytes[cursor++];
            if (index == 4 && current > 0x0f)
            {
                throw new PantsCorruptionException("SST varint overflows u32.");
            }

            value |= (uint)(current & 0x7f) << (index * 7);
            if ((current & 0x80) == 0)
            {
                if (index > 0 && current == 0)
                {
                    throw new PantsCorruptionException("SST varint is noncanonical.");
                }

                return value;
            }
        }

        throw new PantsCorruptionException("SST varint is unterminated.");
    }

    internal static long MeasureDecodedEntryBytes(IReadOnlyList<SstEntry> entries) =>
        checked(DecodedCollectionOverheadBytes + entries.Sum(static entry =>
            (long)entry.Key.Length + (entry.Value?.Length ?? 0) + DecodedEntryOverheadBytes));

    static void DecodeEntries(ReadOnlySpan<byte> block, List<SstEntry> entries,
        long maximumDecodedEntryBytes = DefaultMaximumDecodedEntryBytes, byte[]? pointKey = null)
    {
        var entryCount = PreflightDecodedEntries(block, maximumDecodedEntryBytes, out var maximumKeyLength, out _);
        entries.EnsureCapacity(checked(entries.Count + (pointKey is null ? entryCount : Math.Min(entryCount, 3))));
        if (block.Length < 2 * sizeof(uint) || block.Length > DiskFormat.MaximumDecodedBlockBytes)
        {
            throw new PantsCorruptionException("SST data block has no bounded restart directory.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(block[(block.Length - sizeof(uint))..]);
        var directoryBytes = ((ulong)count + 1) * sizeof(uint);
        if (count == 0 || directoryBytes >= (ulong)block.Length)
        {
            throw new PantsCorruptionException("SST restart directory exceeds its data block.");
        }

        var dataEnd = block.Length - (int)directoryBytes;
        var cursor = 0;
        var previousKey = pointKey is null ? [] : new byte[maximumKeyLength];
        var previousKeyLength = 0;
        ReadOnlySpan<byte> previousValue = default;
        ulong previousSequence = 0;
        ulong? previousExpiration = null;
        byte previousEntryType = 0;
        var hasPreviousEntry = false;
        uint previousOffset = 0;
        for (var restartIndex = 0; restartIndex < (int)count; restartIndex++)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(block[(dataEnd + restartIndex * sizeof(uint))..]);
            var offset = word & ~ContinuedKeyFlag;
            var continued = (word & ContinuedKeyFlag) != 0;
            var intervalEnd = restartIndex + 1 == (int)count
                ? dataEnd
                : (int)(BinaryPrimitives.ReadUInt32LittleEndian(
                    block[(dataEnd + (restartIndex + 1) * sizeof(uint))..]) & ~ContinuedKeyFlag);
            if ((restartIndex == 0 && word != 0) ||
                (restartIndex > 0 && offset <= previousOffset) ||
                offset != cursor || intervalEnd <= cursor || intervalEnd > dataEnd)
            {
                throw new PantsCorruptionException("SST restart offsets are invalid or do not tile the data extent.");
            }

            var firstInInterval = true;
            while (cursor < intervalEnd)
            {
                var extent = block[..intervalEnd];
                var shared = ReadVarint(extent, ref cursor);
                var keyLength = DecodeInt32(ReadVarint(extent, ref cursor), "key length");
                var valueLength = DecodeInt32(ReadVarint(extent, ref cursor), "value length");
                if (shared > ushort.MaxValue || shared > previousKeyLength ||
                    (firstInInterval && shared != 0) || intervalEnd - cursor < sizeof(ulong) + 1)
                {
                    throw new PantsCorruptionException("SST data entry prefix or header is invalid.");
                }

                var sequence = BinaryPrimitives.ReadUInt64LittleEndian(extent[cursor..]);
                cursor += sizeof(ulong);
                var flags = extent[cursor++];
                if ((flags & 0x7c) != 0)
                {
                    throw new PantsCorruptionException("SST data entry metadata is invalid.");
                }

                var entryType = (byte)(flags & 3);
                if (entryType == 3)
                {
                    throw new PantsCompatibilityException("SST Merge entries have no supported resolution semantics.");
                }

                ulong? expiration = null;
                if ((flags & 0x80) != 0)
                {
                    if (intervalEnd - cursor < sizeof(ulong))
                    {
                        throw new PantsCorruptionException("SST data entry expiration is truncated.");
                    }

                    expiration = BinaryPrimitives.ReadUInt64LittleEndian(extent[cursor..]);
                    cursor += sizeof(ulong);
                }

                if (keyLength > intervalEnd - cursor || valueLength > intervalEnd - cursor - keyLength ||
                    (entryType == 2 && valueLength != 0))
                {
                    throw new PantsCorruptionException("SST data entry is truncated or a delete carries a value.");
                }

                var keyDelta = extent.Slice(cursor, keyLength);
                // The shared prefix is equal by construction; compare only the old and new
                // suffixes before overwriting the scratch key used to validate the next entry.
                var comparison = previousKey.AsSpan((int)shared, previousKeyLength - (int)shared)
                    .SequenceCompareTo(keyDelta);
                var materializedKey = pointKey is null ? new byte[(int)shared + keyLength] : null;
                var key = materializedKey is null
                    ? previousKey.AsSpan(0, (int)shared + keyLength)
                    : materializedKey.AsSpan();
                if (materializedKey is not null)
                {
                    previousKey.AsSpan(0, (int)shared).CopyTo(key);
                }

                keyDelta.CopyTo(key[(int)shared..]);
                cursor += keyLength;
                var value = extent.Slice(cursor, valueLength);
                cursor += valueLength;
                if (firstInInterval && restartIndex > 0 && continued != (comparison == 0))
                {
                    throw new PantsCorruptionException("SST restart continuation does not match adjacent keys.");
                }

                if (hasPreviousEntry)
                {
                    if (comparison > 0 || comparison == 0 && previousSequence < sequence)
                    {
                        throw new PantsCorruptionException("SST data entries are not in canonical key and sequence order.");
                    }

                    if (comparison == 0 && previousSequence == sequence &&
                        (previousEntryType != entryType || previousExpiration != expiration ||
                         !previousValue.SequenceEqual(value)))
                    {
                        throw new PantsCorruptionException(
                            $"SST entries contain conflicting content for the same key at sequence {sequence}.");
                    }
                }

                if (pointKey is null || !hasPreviousEntry || cursor == dataEnd || key.SequenceEqual(pointKey))
                {
                    entries.Add(new SstEntry(materializedKey ?? key.ToArray(), entryType == 2 ? null : value.ToArray(), sequence, expiration, entryType == 2)
                    {
                        EntryType = entryType
                    });
                }

                hasPreviousEntry = true;
                previousValue = value;
                previousSequence = sequence;
                previousExpiration = expiration;
                previousEntryType = entryType;
                previousKeyLength = key.Length;
                if (materializedKey is not null)
                {
                    previousKey = materializedKey;
                }
                firstInInterval = false;
            }

            previousOffset = offset;
        }
    }

    internal static long MeasureDecodedDataBlockBytes(ReadOnlySpan<byte> block)
    {
        _ = PreflightDecodedEntries(block, DefaultMaximumDecodedEntryBytes, out _, out var bytes);
        return bytes;
    }

    static int PreflightDecodedEntries(ReadOnlySpan<byte> block, long maximumDecodedEntryBytes, out int maximumKeyLength, out long decodedBytes)
    {
        maximumKeyLength = 0;
        ArgumentOutOfRangeException.ThrowIfNegative(maximumDecodedEntryBytes);
        if (block.Length < 2 * sizeof(uint) || block.Length > DiskFormat.MaximumDecodedBlockBytes)
        {
            throw new PantsCorruptionException("SST data block has no bounded restart directory.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(block[(block.Length - sizeof(uint))..]);
        var directoryBytes = ((ulong)count + 1) * sizeof(uint);
        if (count == 0 || directoryBytes >= (ulong)block.Length)
        {
            throw new PantsCorruptionException("SST restart directory exceeds its data block.");
        }

        var dataEnd = block.Length - (int)directoryBytes;
        var cursor = 0;
        var previousKeyLength = 0;
        var entryCount = 0;
        decodedBytes = DecodedCollectionOverheadBytes;
        uint previousOffset = 0;
        for (var restartIndex = 0; restartIndex < (int)count; restartIndex++)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(block[(dataEnd + restartIndex * sizeof(uint))..]);
            var offset = word & ~ContinuedKeyFlag;
            var intervalEnd = restartIndex + 1 == (int)count ? dataEnd :
                (int)(BinaryPrimitives.ReadUInt32LittleEndian(block[(dataEnd + (restartIndex + 1) * sizeof(uint))..]) & ~ContinuedKeyFlag);
            if ((restartIndex == 0 && word != 0) || (restartIndex > 0 && offset <= previousOffset) ||
                offset != cursor || intervalEnd <= cursor || intervalEnd > dataEnd)
            {
                throw new PantsCorruptionException("SST restart offsets are invalid or do not tile the data extent.");
            }

            var firstInInterval = true;
            while (cursor < intervalEnd)
            {
                var extent = block[..intervalEnd];
                var shared = ReadVarint(extent, ref cursor);
                var keyLength = DecodeInt32(ReadVarint(extent, ref cursor), "key length");
                var valueLength = DecodeInt32(ReadVarint(extent, ref cursor), "value length");
                if (shared > ushort.MaxValue || shared > previousKeyLength || firstInInterval && shared != 0 ||
                    intervalEnd - cursor < sizeof(ulong) + 1)
                {
                    throw new PantsCorruptionException("SST data entry prefix or header is invalid.");
                }

                cursor += sizeof(ulong);
                var flags = extent[cursor++];
                if ((flags & 0x7c) != 0)
                {
                    throw new PantsCorruptionException("SST data entry metadata is invalid.");
                }

                var entryType = flags & 3;
                if (entryType == 3)
                {
                    throw new PantsCompatibilityException("SST Merge entries have no supported resolution semantics.");
                }

                if ((flags & 0x80) != 0)
                {
                    if (intervalEnd - cursor < sizeof(ulong))
                    {
                        throw new PantsCorruptionException("SST data entry expiration is truncated.");
                    }

                    cursor += sizeof(ulong);
                }

                if (keyLength > intervalEnd - cursor || valueLength > intervalEnd - cursor - keyLength ||
                    entryType == 2 && valueLength != 0)
                {
                    throw new PantsCorruptionException("SST data entry is truncated or a delete carries a value.");
                }

                previousKeyLength = checked((int)shared + keyLength);
                maximumKeyLength = Math.Max(maximumKeyLength, previousKeyLength);
                decodedBytes = checked(decodedBytes + previousKeyLength + valueLength + DecodedEntryOverheadBytes);
                if (decodedBytes > maximumDecodedEntryBytes)
                {
                    throw new PantsResourceLimitException("Decoded SST entries exceed the available working memory budget.");
                }

                cursor += keyLength + valueLength;
                entryCount++;
                firstInInterval = false;
            }

            previousOffset = offset;
        }

        return entryCount;
    }

    internal static void ValidateAdjacentEntries(SstEntry previous, SstEntry current)
    {
        var comparison = previous.Key.AsSpan().SequenceCompareTo(current.Key);
        if (comparison > 0 || (comparison == 0 && previous.Sequence < current.Sequence))
        {
            throw new PantsCorruptionException("SST data entries are not in canonical key and sequence order.");
        }

        if (comparison == 0 && previous.Sequence == current.Sequence)
        {
            RequireMatchingContent(previous, current);
        }
    }

    internal static void RequireMatchingContent(SstEntry first, SstEntry duplicate)
    {
        if (first.EntryType != duplicate.EntryType || first.Expiration != duplicate.Expiration ||
            (first.Value is null) != (duplicate.Value is null) ||
            !first.Value.AsSpan().SequenceEqual(duplicate.Value))
        {
            throw new PantsCorruptionException(
                $"SST entries contain conflicting content for the same key at sequence {first.Sequence}.");
        }
    }

    static SstBlockHandle AppendBlock(Stream stream, byte[] raw, PantsPerformanceGoal performanceGoal)
    {
        var offset = checked((ulong)stream.Position);
        var withTrailer = SstBlockCodec.CompressWithTrailer(raw, performanceGoal);
        DiskFormat.WriteUInt32(stream, (uint)withTrailer.Length);
        stream.Write(withTrailer);
        return new SstBlockHandle(offset, checked((ulong)withTrailer.Length + 4));
    }

    internal static byte[] ReadBlock(byte[] file, SstBlockHandle handle)
    {
        if (handle.Offset > (ulong)file.Length || handle.Size < 9 || handle.Size > (ulong)file.Length - handle.Offset)
        {
            throw new PantsCorruptionException("SST block handle is outside the file.");
        }

        var offset = DecodeInt32(handle.Offset, "block offset");
        var encodedLength = DecodeInt32(
            BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset, 4)),
            "encoded block length");
        if ((ulong)encodedLength + 4 != handle.Size || encodedLength < 5)
        {
            throw new PantsCorruptionException("SST block length does not match its handle.");
        }

        var encoded = file.AsSpan(offset + 4, encodedLength);
        return SstBlockCodec.DecompressWithTrailer(encoded);
    }

    internal static byte[] ReadBlock(
        SafeFileHandle file,
        long fileLength,
        SstBlockHandle handle)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (handle.Offset > (ulong)fileLength ||
            handle.Size < 9 ||
            handle.Size > int.MaxValue ||
            handle.Size > (ulong)fileLength - handle.Offset)
        {
            throw new PantsCorruptionException("SST block handle is outside the file.");
        }

        var encoded = PositionalFile.ReadExactly(
            file,
            checked((long)handle.Offset),
            checked((int)handle.Size));
        return ReadBlock(encoded, new SstBlockHandle(0, handle.Size));
    }

    static byte[] EncodeMetadata(
        IReadOnlyList<SstEntry> entries,
        IReadOnlyList<RangeTombstone> tombstones,
        SstBlockHandle? rangeHandle,
        SstIndexKind indexKind)
    {
        var (smallestKey, largestKey) = GetKeyBounds(entries, tombstones);
        using var output = new MemoryStream();
        DiskFormat.WriteUInt32(output, DiskFormat.SstFormatVersion);
        output.WriteByte((byte)indexKind);
        output.WriteByte(smallestKey is not null ? (byte)1 : (byte)0);
        WriteUInt16(output, 0);
        WriteHandle(output, rangeHandle ?? default);
        if (smallestKey is not null)
        {
            WriteLengthPrefixed(output, smallestKey);
            WriteLengthPrefixed(output, largestKey!);
        }

        return output.ToArray();
    }

    internal static (byte[]? SmallestKey, byte[]? LargestKey) GetKeyBounds(
        IReadOnlyList<SstEntry> entries,
        IReadOnlyList<RangeTombstone> tombstones)
    {
        byte[]? smallestKey = null;
        byte[]? largestKey = null;

        void Include(byte[] key)
        {
            if (smallestKey is null ||
                ByteArrayComparer.Instance.Compare(key, smallestKey) < 0)
            {
                smallestKey = key;
            }

            if (largestKey is null ||
                ByteArrayComparer.Instance.Compare(key, largestKey) > 0)
            {
                largestKey = key;
            }
        }

        foreach (var entry in entries)
        {
            Include(entry.Key);
        }

        foreach (var tombstone in tombstones)
        {
            Include(tombstone.Start);
            Include(tombstone.End);
        }

        return (smallestKey, largestKey);
    }

    internal static SstMetadata DecodeMetadata(byte[] metadata)
    {
        if (metadata.Length == 20)
        {
            throw new PantsCompatibilityException("Legacy SST metadata is incompatible with V5.");
        }

        if (metadata.Length < 24)
        {
            throw new PantsCorruptionException("SST metadata block is truncated.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(metadata);
        if (version != DiskFormat.SstFormatVersion)
        {
            throw new PantsCompatibilityException(
                $"Unsupported SST metadata format version '{version}'.");
        }

        var rawIndexKind = metadata[4];
        var flags = metadata[5];
        if (rawIndexKind is not (0 or 1) || (flags & ~1) != 0 || metadata[6] != 0 || metadata[7] != 0)
        {
            throw new PantsCorruptionException("SST metadata flags, index kind, or reserved bytes are invalid.");
        }

        // A handle is present exactly when its size is nonzero (sst.md section 5.1). Offset 0 is a
        // valid location: a tombstone-only SST has its range block first in the file.
        var rawRangeHandle = ReadHandle(metadata, 8);
        if (rawRangeHandle.Size == 0 && rawRangeHandle.Offset != 0)
        {
            throw new PantsCorruptionException("SST range-tombstone handle is only partially present.");
        }

        SstBlockHandle? rangeHandle = rawRangeHandle.Size == 0
            ? null
            : rawRangeHandle;
        var cursor = 24;
        byte[]? smallestKey = null;
        byte[]? largestKey = null;
        if ((flags & 1) == 0)
        {
            if (cursor != metadata.Length)
            {
                throw new PantsCorruptionException("SST metadata without a key range has trailing bytes.");
            }
        }
        else
        {
            smallestKey = ReadLengthPrefixed(metadata, ref cursor);
            largestKey = ReadLengthPrefixed(metadata, ref cursor);
            if (cursor != metadata.Length ||
                ByteArrayComparer.Instance.Compare(smallestKey, largestKey) > 0)
            {
                throw new PantsCorruptionException("SST metadata key range is malformed or inverted.");
            }
        }

        return new SstMetadata((SstIndexKind)rawIndexKind, rangeHandle, smallestKey, largestKey);
    }

    internal static void ValidateBlockBlooms(byte[] bytes, int expectedBlockCount)
    {
        if (bytes.Length < sizeof(uint))
        {
            throw new PantsCorruptionException("SST block-bloom header is truncated.");
        }

        var blockCount = DecodeInt32(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            "block-bloom count");
        if (blockCount != expectedBlockCount || blockCount > (bytes.Length - sizeof(uint)) / sizeof(uint))
        {
            throw new PantsCorruptionException("SST block-bloom count or offset table is invalid.");
        }

        var headerLength = sizeof(uint) + blockCount * sizeof(uint);

        ReadOnlySpan<byte> bloomData = bytes.AsSpan(headerLength);
        var previousOffset = -1;
        for (var index = 0; index < blockCount; index++)
        {
            var offset = DecodeInt32(
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(sizeof(uint) + index * sizeof(uint))),
                "block-bloom offset");
            if ((index == 0 && offset != 0) || offset <= previousOffset || offset > bloomData.Length)
            {
                throw new PantsCorruptionException("SST block-bloom offset table is invalid.");
            }

            previousOffset = offset;
        }

        for (var index = 0; index < blockCount; index++)
        {
            var start = DecodeInt32(
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(sizeof(uint) + index * sizeof(uint))),
                "block-bloom offset");
            var end = index + 1 == blockCount
                ? bloomData.Length
                : DecodeInt32(
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(sizeof(uint) + (index + 1) * sizeof(uint))),
                    "block-bloom offset");
            ValidateBloomFilter(bloomData[start..end]);
        }
    }

    static void ValidateBloomFilter(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 9)
        {
            throw new PantsCorruptionException("SST bloom filter is truncated.");
        }

        var numberOfBits = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var hashFunctions = bytes[8];
        var expectedBytes = checked(((long)numberOfBits + 7) / 8);
        if (numberOfBits == 0 || hashFunctions is < 1 or > 8 || bytes.Length - 9 != expectedBytes)
        {
            throw new PantsCorruptionException("SST bloom filter parameters are invalid.");
        }
    }

    internal static void ValidateBlockCoverage(
        long footerOffset,
        SstBlockHandle metadata,
        SstBlockHandle index,
        SstBlockHandle? trie,
        SstBlockHandle? bloom,
        SstBlockHandle? range,
        List<(byte[] FirstKey, SstBlockHandle Handle)> dataBlocks)
    {
        var handles = new List<SstBlockHandle>(checked(2 + dataBlocks.Count + 3))
        {
            metadata,
            index
        };
        handles.AddRange(dataBlocks.Select(static block => block.Handle));
        if (trie.HasValue)
        {
            handles.Add(trie.Value);
        }

        if (bloom.HasValue)
        {
            handles.Add(bloom.Value);
        }

        if (range.HasValue)
        {
            handles.Add(range.Value);
        }

        handles.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        if (handles[0].Offset != 0)
        {
            throw new PantsCorruptionException("SST block references leave unreferenced leading bytes.");
        }

        for (var position = 1; position < handles.Count; position++)
        {
            var previousEnd = checked(handles[position - 1].Offset + handles[position - 1].Size);
            if (previousEnd != handles[position].Offset)
            {
                throw new PantsCorruptionException(
                    previousEnd > handles[position].Offset
                        ? "SST block references overlap."
                        : "SST block references leave unreferenced bytes.");
            }
        }

        var last = handles[^1];
        if (checked(last.Offset + last.Size) != checked((ulong)footerOffset))
        {
            throw new PantsCorruptionException("SST block references do not exactly reach the footer.");
        }
    }

    static void ValidateEntryOrdering(List<SstEntry> entries)
    {
        for (var index = 1; index < entries.Count; index++)
        {
            ValidateAdjacentEntries(entries[index - 1], entries[index]);
        }
    }

    static void ValidateMetadataKeyRange(
        SstMetadata metadata,
        List<SstEntry> entries,
        List<RangeTombstone> tombstones)
    {
        var keys = entries.Select(static entry => entry.Key)
            .Concat(tombstones.SelectMany(static tombstone => new[] { tombstone.Start, tombstone.End }))
            .OrderBy(static key => key, ByteArrayComparer.Instance)
            .ToArray();
        if (keys.Length == 0)
        {
            if (metadata.SmallestKey is not null || metadata.LargestKey is not null)
            {
                throw new PantsCorruptionException("Empty SST metadata unexpectedly declares a key range.");
            }

            return;
        }

        if (metadata.SmallestKey is null ||
            metadata.LargestKey is null ||
            !metadata.SmallestKey.AsSpan().SequenceEqual(keys[0]) ||
            !metadata.LargestKey.AsSpan().SequenceEqual(keys[^1]))
        {
            throw new PantsCorruptionException("SST metadata key range does not match its contents.");
        }
    }

    static byte[] EncodeIndex(IEnumerable<(byte[] FirstKey, SstBlockHandle Handle)> entries)
    {
        using var output = new MemoryStream();
        foreach (var (key, handle) in entries)
        {
            WriteLengthPrefixed(output, key);
            WriteHandle(output, handle);
        }

        return output.ToArray();
    }

    internal static List<(byte[] FirstKey, SstBlockHandle Handle)> DecodeIndex(byte[] bytes)
    {
        var output = new List<(byte[], SstBlockHandle)>();
        var cursor = 0;
        while (cursor < bytes.Length)
        {
            if (bytes.Length - cursor < 4)
            {
                throw new PantsCorruptionException("SST index key length is truncated.");
            }

            var length = DecodeInt32(
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4)),
                "index key length");
            cursor += 4;
            if (length > bytes.Length - cursor - 16)
            {
                throw new PantsCorruptionException("SST index entry is truncated.");
            }

            var key = bytes.AsSpan(cursor, length).ToArray();
            cursor += length;
            var handle = ReadHandle(bytes, cursor);
            cursor += 16;
            if (output.Count > 0 && output[^1].Item1.AsSpan().SequenceCompareTo(key) > 0)
            {
                throw new PantsCorruptionException("SST index first keys are not sorted in ascending order.");
            }

            output.Add((key, handle));
        }

        return output;
    }

    internal static TrieIndex? DecodeTrieIndex(
        byte[] file,
        SstIndexKind indexKind,
        SstBlockHandle? trieHandle,
        IReadOnlyList<(byte[] FirstKey, SstBlockHandle Handle)> blockIndex)
    {
        var trie = trieHandle is { } handle ? ReadBlock(file, handle) : null;
        return DecodeTrieIndex(indexKind, trie, blockIndex);
    }

    internal static TrieIndex? DecodeTrieIndex(
        SstIndexKind indexKind,
        byte[]? trie,
        IReadOnlyList<(byte[] FirstKey, SstBlockHandle Handle)> blockIndex,
        SstMetadataAllocation? allocation = null)
    {
        if (trie is not null)
        {
            allocation?.Reserve(checked(trie.Length * 32L));
        }
        return (indexKind, trie) switch
        {
            (SstIndexKind.Trie, { } bytes) => TrieIndex.Decode(
                bytes,
                blockIndex.Select(static entry => entry.FirstKey).ToArray(),
                allocation),
            (SstIndexKind.Trie, null) => throw new PantsCorruptionException(
                "Trie-selected SST metadata is missing its trie footer handle."),
            (SstIndexKind.Sparse, not null) => throw new PantsCorruptionException(
                "Sparse-selected SST metadata unexpectedly carries a trie footer handle."),
            (SstIndexKind.Sparse, null) => null,
            _ => throw new PantsCorruptionException("SST metadata selects an unsupported index kind.")
        };
    }

    internal static int FindFloorBlock(
        IReadOnlyList<(byte[] FirstKey, SstBlockHandle Handle)> index,
        ReadOnlySpan<byte> key)
    {
        var low = 0;
        var high = index.Count - 1;
        var result = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (key.SequenceCompareTo(index[middle].FirstKey) < 0)
            {
                high = middle - 1;
            }
            else
            {
                result = middle;
                low = middle + 1;
            }
        }

        return result;
    }

    static byte[] EncodeBlockBlooms(IReadOnlyList<IReadOnlyList<byte[]>> blockKeys)
    {
        var blooms = blockKeys.Select(EncodeBloom).ToArray();
        using var output = new MemoryStream();
        DiskFormat.WriteUInt32(output, checked((uint)blooms.Length));
        uint offset = 0;
        foreach (var bloom in blooms)
        {
            DiskFormat.WriteUInt32(output, offset);
            offset = checked(offset + (uint)bloom.Length);
        }

        foreach (var bloom in blooms)
        {
            output.Write(bloom);
        }

        return output.ToArray();
    }

    static byte[] EncodeBloom(IReadOnlyList<byte[]> keys)
    {
        var estimatedKeys = Math.Max(1, keys.Count);
        var numberOfBits = Math.Max(
            64,
            checked((int)Math.Ceiling(
                -estimatedKeys * Math.Log(0.01) / Math.Pow(Math.Log(2), 2))));
        var hashFunctions = checked((byte)Math.Clamp(
            (int)Math.Round(
                (double)numberOfBits / estimatedKeys * Math.Log(2),
                MidpointRounding.AwayFromZero),
            1,
            8));
        var bits = new byte[(numberOfBits + 7) / 8];
        foreach (var key in keys)
        {
            SetBloomBits(bits, numberOfBits, hashFunctions, key);
        }

        using var output = new MemoryStream(checked(9 + bits.Length));
        DiskFormat.WriteUInt32(output, checked((uint)numberOfBits));
        DiskFormat.WriteUInt32(output, checked((uint)keys.Count));
        output.WriteByte(hashFunctions);
        output.Write(bits);
        return output.ToArray();
    }

    internal static bool BloomMightContain(
        ReadOnlySpan<byte> serializedBlooms,
        int blockIndex,
        ReadOnlySpan<byte> key)
    {
        if (serializedBlooms.Length < sizeof(uint))
        {
            throw new PantsCorruptionException("SST block-bloom header is truncated.");
        }

        var blockCount = DecodeInt32(
            BinaryPrimitives.ReadUInt32LittleEndian(serializedBlooms),
            "block-bloom count");
        if (blockIndex >= blockCount)
        {
            return true;
        }

        if (blockIndex < 0 || blockCount > (serializedBlooms.Length - sizeof(uint)) / sizeof(uint))
        {
            throw new PantsCorruptionException("SST block-bloom count or offset table is invalid.");
        }

        var headerLength = sizeof(uint) + blockCount * sizeof(uint);
        var startOffset = DecodeInt32(
            BinaryPrimitives.ReadUInt32LittleEndian(
                serializedBlooms.Slice(sizeof(uint) + blockIndex * sizeof(uint), sizeof(uint))),
            "block-bloom offset");
        var start = AddBloomOffset(headerLength, startOffset, serializedBlooms.Length);
        var end = blockIndex + 1 == blockCount
            ? serializedBlooms.Length
            : AddBloomOffset(
                headerLength,
                DecodeInt32(
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        serializedBlooms.Slice(
                            sizeof(uint) + (blockIndex + 1) * sizeof(uint),
                            sizeof(uint))),
                    "block-bloom offset"),
                serializedBlooms.Length);
        if (end < start)
        {
            throw new PantsCorruptionException("SST block-bloom offset table is invalid.");
        }

        var bloom = serializedBlooms[start..end];
        ValidateBloomFilter(bloom);
        var numberOfBits = DecodeInt32(
            BinaryPrimitives.ReadUInt32LittleEndian(bloom),
            "bloom bit count");
        var hashFunctions = bloom[8];
        var bits = bloom[9..];
        var firstHash = Hash(key, BloomSeedOne);
        var secondHash = Hash(key, BloomSeedTwo);
        for (ulong hashIndex = 0; hashIndex < hashFunctions; hashIndex++)
        {
            var bitIndex = unchecked(firstHash + hashIndex * secondHash) % checked((ulong)numberOfBits);
            if ((bits[checked((int)(bitIndex / 8))] & (1 << checked((int)(bitIndex % 8)))) == 0)
            {
                return false;
            }
        }

        return true;
    }

    static void SetBloomBits(
        Span<byte> bits,
        int numberOfBits,
        byte hashFunctions,
        ReadOnlySpan<byte> key)
    {
        var firstHash = Hash(key, BloomSeedOne);
        var secondHash = Hash(key, BloomSeedTwo);
        for (ulong hashIndex = 0; hashIndex < hashFunctions; hashIndex++)
        {
            var bitIndex = unchecked(firstHash + hashIndex * secondHash) % checked((ulong)numberOfBits);
            bits[checked((int)(bitIndex / 8))] |= checked((byte)(1 << checked((int)(bitIndex % 8))));
        }
    }

    static ulong Hash(ReadOnlySpan<byte> key, ulong seed)
    {
        var hasher = new XxHash3(unchecked((long)seed));
        hasher.Append(key);
        return hasher.GetCurrentHashAsUInt64();
    }

    static byte[] EncodeRangeTombstones(IReadOnlyList<RangeTombstone> tombstones)
    {
        using var output = new MemoryStream();
        DiskFormat.WriteUInt32(output, (uint)tombstones.Count);
        foreach (var tombstone in tombstones)
        {
            WriteLengthPrefixed(output, tombstone.Start);
            WriteLengthPrefixed(output, tombstone.End);
            DiskFormat.WriteUInt64(output, tombstone.Sequence);
        }

        return output.ToArray();
    }

    internal static List<RangeTombstone> DecodeRangeTombstones(byte[] bytes)
    {
        if (bytes.Length < 4)
        {
            throw new PantsCorruptionException("SST range tombstone block is truncated.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var cursor = 4;
        if (count > (bytes.Length - cursor) / 16)
        {
            throw new PantsCorruptionException("SST range tombstone count exceeds its block.");
        }
        var output = new List<RangeTombstone>(DecodeInt32(count, "range-tombstone count"));
        for (var index = 0; index < count; index++)
        {
            var start = ReadLengthPrefixed(bytes, ref cursor);
            var end = ReadLengthPrefixed(bytes, ref cursor);
            if (bytes.Length - cursor < 8)
            {
                throw new PantsCorruptionException("SST range tombstone sequence is truncated.");
            }

            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(cursor, 8));
            cursor += 8;
            output.Add(new RangeTombstone(start, end, sequence));
        }

        if (cursor != bytes.Length)
        {
            throw new PantsCorruptionException("SST range tombstone block has trailing bytes.");
        }

        return output;
    }

    static byte[] EncodeFooter(
        SstBlockHandle meta,
        SstBlockHandle index,
        SstBlockHandle? trie,
        SstBlockHandle bloom)
    {
        var footer = new byte[DiskFormat.SstFooterSize];
        WriteHandle(footer, 0, meta);
        WriteHandle(footer, 16, index);
        if (trie.HasValue)
        {
            WriteHandle(footer, 32, trie.Value);
        }

        WriteHandle(footer, 48, bloom);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(64), DiskFormat.SstFormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(68), DiskFormat.SstFooterSize);
        BinaryPrimitives.WriteUInt64LittleEndian(footer.AsSpan(72), DiskFormat.SstFooterMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(80), DiskFormat.Crc32C(footer.AsSpan(0, 80)));
        return footer;
    }

    internal static void ValidateFooter(ReadOnlySpan<byte> footer)
    {
        if (footer.Length != DiskFormat.SstFooterSize)
        {
            throw new PantsCorruptionException("SST V5 footer size is invalid.");
        }

        var magic = BinaryPrimitives.ReadUInt64LittleEndian(footer[72..80]);
        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer[80..84]);
        if (magic != DiskFormat.SstFooterMagic || DiskFormat.Crc32C(footer[..80]) != storedCrc)
        {
            // sst.md section 7.1 step 3b: only the file's own last eight bytes can be a legacy
            // footer magic. The V5 magic field sits before the checksum, so an intact magic with a
            // damaged handle is corruption, not an older format.
            if (BinaryPrimitives.ReadUInt64LittleEndian(footer[^sizeof(ulong)..]) ==
                DiskFormat.SstFooterMagic)
            {
                throw new PantsCompatibilityException(
                    "SST V5 footer checksum mismatch, but the file ends with a legacy magic value.");
            }

            throw new PantsCorruptionException("SST V5 footer checksum mismatch.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(footer[68..72]) != DiskFormat.SstFooterSize ||
            magic != DiskFormat.SstFooterMagic)
        {
            throw new PantsCorruptionException("SST V5 footer format or magic is invalid.");
        }

        var formatVersion = BinaryPrimitives.ReadUInt32LittleEndian(footer[64..68]);
        if (formatVersion != DiskFormat.SstFormatVersion)
        {
            throw new PantsCompatibilityException(
                $"Unsupported SST V5 footer format version '{formatVersion}'.");
        }
    }

    internal static SstBlockHandle ReadHandle(ReadOnlySpan<byte> bytes, int offset) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8)),
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset + 8, 8)));

    internal static SstBlockHandle? ReadOptionalHandle(
        ReadOnlySpan<byte> bytes,
        int offset,
        string description)
    {
        var handle = ReadHandle(bytes, offset);
        if (handle.Offset == 0 && handle.Size == 0)
        {
            return null;
        }

        if (handle.Size == 0)
        {
            throw new PantsCorruptionException($"SST footer {description} handle is only partially present.");
        }

        return handle;
    }

    static void WriteHandle(Stream stream, SstBlockHandle handle)
    {
        DiskFormat.WriteUInt64(stream, handle.Offset);
        DiskFormat.WriteUInt64(stream, handle.Size);
    }

    static void WriteHandle(Span<byte> bytes, int offset, SstBlockHandle handle)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(offset, 8), handle.Offset);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(offset + 8, 8), handle.Size);
    }

    static void WriteLengthPrefixed(Stream stream, byte[] bytes)
    {
        DiskFormat.WriteUInt32(stream, (uint)bytes.Length);
        stream.Write(bytes);
    }

    static byte[] ReadLengthPrefixed(byte[] bytes, ref int cursor)
    {
        if (bytes.Length - cursor < 4)
        {
            throw new PantsCorruptionException("SST length-prefixed field is truncated.");
        }

        var length = DecodeInt32(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4)),
            "length-prefixed field size");
        cursor += 4;
        if (length > bytes.Length - cursor)
        {
            throw new PantsCorruptionException("SST length-prefixed value is truncated.");
        }

        var value = bytes.AsSpan(cursor, length).ToArray();
        cursor += length;
        return value;
    }

    static int AddBloomOffset(int headerLength, int offset, int totalLength)
    {
        if (offset > totalLength - headerLength)
        {
            throw new PantsCorruptionException("SST block-bloom offset table is invalid.");
        }

        return headerLength + offset;
    }

    static int DecodeInt32(uint value, string field)
    {
        if (value > int.MaxValue)
        {
            throw new PantsCorruptionException($"SST {field} exceeds the supported size.");
        }

        return (int)value;
    }

    static int DecodeInt32(ulong value, string field)
    {
        if (value > int.MaxValue)
        {
            throw new PantsCorruptionException($"SST {field} exceeds the supported size.");
        }

        return (int)value;
    }

    static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
