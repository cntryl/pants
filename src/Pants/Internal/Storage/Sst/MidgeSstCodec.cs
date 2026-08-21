using System.Buffers.Binary;
using System.IO.Hashing;
using K4os.Compression.LZ4;
using ZstdSharp;

namespace Pants;

internal static class MidgeSstCodec
{
    private const int TargetBlockSize = 64 * 1024;
    private const int EntryHeaderSize = 26;
    private const int MinimumCompressionInputSize = 256;
    private const int AdaptiveMinimumSavings = 256;
    private const float AdaptiveMaximumRatio = 0.95F;
    private const ulong BloomSeedOne = 0x9E37_79B1_85EB_CA87;
    private const ulong BloomSeedTwo = 0xC2B2_AE3D_27D4_EB4F;

    public static byte[] Encode(
        IReadOnlyList<MidgeSstEntry> sourceEntries,
        IReadOnlyList<MidgeRangeTombstone> tombstones,
        PantsPerformanceGoal performanceGoal)
    {
        var entries = sourceEntries
            .OrderBy(entry => entry.Key, ByteArrayComparer.Instance)
            .ThenByDescending(entry => entry.Sequence)
            .ToList();
        using var file = new MemoryStream();
        var index = new List<(byte[] FirstKey, BlockHandle Handle)>();
        var blockKeys = new List<IReadOnlyList<byte[]>>();
        var currentBlockKeys = new List<byte[]>();
        var keyProfiler = new KeyStructureProfiler();
        using var block = new MemoryStream();
        byte[] previousKey = [];
        byte[]? firstKey = null;
        foreach (var entry in entries)
        {
            keyProfiler.Add(entry.Key);
            var encoded = EncodeEntry(previousKey, entry);
            if (block.Length > 0 && block.Length + encoded.Length > TargetBlockSize)
            {
                index.Add((firstKey!, AppendBlock(file, block.ToArray(), performanceGoal)));
                blockKeys.Add(currentBlockKeys.ToArray());
                currentBlockKeys.Clear();
                block.SetLength(0);
                previousKey = [];
                firstKey = null;
                encoded = EncodeEntry(previousKey, entry);
            }

            firstKey ??= entry.Key;
            block.Write(encoded);
            currentBlockKeys.Add(entry.Key);
            previousKey = entry.Key;
        }

        if (block.Length > 0)
        {
            index.Add((firstKey!, AppendBlock(file, block.ToArray(), performanceGoal)));
            blockKeys.Add(currentBlockKeys.ToArray());
        }

        BlockHandle? rangeHandle = tombstones.Count == 0
            ? null
            : AppendBlock(file, EncodeRangeTombstones(tombstones), performanceGoal);
        MidgeSstIndexKind indexKind = MidgeSstIndexTuner.Decide(keyProfiler.Finish());
        BlockHandle? trieHandle = indexKind == MidgeSstIndexKind.Trie
            ? AppendBlock(
                file,
                MidgeTrieIndex.Encode(index.Select(static entry => entry.FirstKey).ToArray()),
                performanceGoal)
            : null;
        var blockBloomHandle = AppendBlock(file, EncodeBlockBlooms(blockKeys), performanceGoal);
        var metaHandle = AppendBlock(
            file,
            EncodeMetadata(entries, tombstones, rangeHandle, indexKind),
            performanceGoal);
        var indexHandle = AppendBlock(file, EncodeIndex(index), performanceGoal);
        file.Write(EncodeFooter(metaHandle, indexHandle, trieHandle, blockBloomHandle));
        return file.ToArray();
    }

    public static MidgeSstContents Decode(byte[] bytes)
    {
        if (bytes.Length < MidgeDiskFormat.SstFooterSize)
        {
            throw new PantsStorageException("SST is shorter than its V4 footer.");
        }

        var footerOffset = bytes.Length - MidgeDiskFormat.SstFooterSize;
        var footer = bytes.AsSpan(footerOffset);
        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer[80..84]);
        if (MidgeDiskFormat.Crc32C(footer[..80]) != storedCrc)
        {
            throw new PantsStorageException("SST V4 footer checksum mismatch.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(footer[64..68]) != MidgeDiskFormat.SstFormatVersion ||
            BinaryPrimitives.ReadUInt32LittleEndian(footer[68..72]) != MidgeDiskFormat.SstFooterSize ||
            BinaryPrimitives.ReadUInt64LittleEndian(footer[72..80]) != MidgeDiskFormat.SstFooterMagic)
        {
            throw new PantsStorageException("SST V4 footer format or magic is invalid.");
        }

        var metaHandle = ReadHandle(footer, 0);
        var indexHandle = ReadHandle(footer, 16);
        BlockHandle? trieHandle = ReadOptionalHandle(footer, 32, "trie");
        BlockHandle? bloomHandle = ReadOptionalHandle(footer, 48, "block bloom");
        var meta = ReadBlock(bytes, metaHandle);
        var index = DecodeIndex(ReadBlock(bytes, indexHandle));
        SstMetadata metadata = DecodeMetadata(meta);
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
        var entries = new List<MidgeSstEntry>();
        foreach (var (firstKey, handle) in index)
        {
            int firstEntryIndex = entries.Count;
            DecodeEntries(ReadBlock(bytes, handle), entries);
            if (entries.Count == firstEntryIndex ||
                !entries[firstEntryIndex].Key.AsSpan().SequenceEqual(firstKey))
            {
                throw new PantsStorageException("SST index first key does not match its data block.");
            }
        }

        ValidateEntryOrdering(entries);
        ValidateMetadataKeyRange(metadata, entries, tombstones);

        return new MidgeSstContents(entries, tombstones, index.Count);
    }

    internal static SstPointReadDecision GetPointReadDecision(
        byte[] bytes,
        ReadOnlySpan<byte> key)
    {
        if (bytes.Length < MidgeDiskFormat.SstFooterSize)
        {
            throw new PantsStorageException("SST is shorter than its V4 footer.");
        }

        ReadOnlySpan<byte> footer = bytes.AsSpan(bytes.Length - MidgeDiskFormat.SstFooterSize);
        List<(byte[] FirstKey, BlockHandle Handle)> index = DecodeIndex(
            ReadBlock(bytes, ReadHandle(footer, 16)));
        SstMetadata metadata = DecodeMetadata(ReadBlock(bytes, ReadHandle(footer, 0)));
        MidgeTrieIndex? trieIndex = DecodeTrieIndex(
            bytes,
            metadata.IndexKind,
            ReadOptionalHandle(footer, 32, "trie"),
            index);
        BlockHandle? bloomHandle = ReadOptionalHandle(footer, 48, "block bloom");
        if (index.Count == 0 || bloomHandle is null)
        {
            return new SstPointReadDecision(0, 0, 0, false, -1, 0);
        }

        int trieCandidate = trieIndex?.FindFloorBlock(key) ?? -1;
        int candidate = trieCandidate >= 0 ? trieCandidate : FindFloorBlock(index, key);

        if (candidate < 0)
        {
            return new SstPointReadDecision(0, 0, 0, false, -1, 0);
        }

        bool mightContain = BloomMightContain(
            ReadBlock(bytes, bloomHandle.Value),
            candidate,
            key);
        BlockHandle candidateHandle = index[candidate].Handle;
        int blockSizeBytes = checked((int)candidateHandle.Size);
        return mightContain
            ? new SstPointReadDecision(1, 1, 1, false, candidate, blockSizeBytes)
            : new SstPointReadDecision(1, 1, 0, true, candidate, blockSizeBytes);
    }

    internal static MidgeSstIndexKind GetIndexKind(byte[] bytes)
    {
        if (bytes.Length < MidgeDiskFormat.SstFooterSize)
        {
            throw new PantsStorageException("SST is shorter than its V4 footer.");
        }

        ReadOnlySpan<byte> footer = bytes.AsSpan(bytes.Length - MidgeDiskFormat.SstFooterSize);
        SstMetadata metadata = DecodeMetadata(ReadBlock(bytes, ReadHandle(footer, 0)));
        List<(byte[] FirstKey, BlockHandle Handle)> index = DecodeIndex(
            ReadBlock(bytes, ReadHandle(footer, 16)));
        _ = DecodeTrieIndex(
            bytes,
            metadata.IndexKind,
            ReadOptionalHandle(footer, 32, "trie"),
            index);
        return metadata.IndexKind;
    }

    internal static void ValidatePointReadDataBlock(byte[] bytes, int blockIndex)
    {
        _ = ReadPointReadDataBlock(bytes, blockIndex);
    }

    internal static byte[] ReadPointReadDataBlock(byte[] bytes, int blockIndex)
    {
        ReadOnlySpan<byte> footer = bytes.AsSpan(bytes.Length - MidgeDiskFormat.SstFooterSize);
        List<(byte[] FirstKey, BlockHandle Handle)> index = DecodeIndex(
            ReadBlock(bytes, ReadHandle(footer, 16)));
        if ((uint)blockIndex >= (uint)index.Count)
        {
            throw new PantsStorageException("SST point-read block index is invalid.");
        }

        return ReadBlock(bytes, index[blockIndex].Handle);
    }

    private static byte[] EncodeEntry(byte[] previousKey, MidgeSstEntry entry)
    {
        var shared = 0;
        var maxShared = Math.Min(Math.Min(previousKey.Length, entry.Key.Length), ushort.MaxValue);
        while (shared < maxShared && previousKey[shared] == entry.Key[shared])
        {
            shared++;
        }

        int keyLength = entry.Key.Length - shared;
        var value = entry.Value ?? [];
        bool extendedLengths = keyLength > ushort.MaxValue;
        using var output = new MemoryStream(checked(
            EntryHeaderSize +
            (extendedLengths ? 2 * sizeof(uint) : 0) +
            keyLength +
            value.Length));
        WriteUInt16(output, (ushort)shared);
        WriteUInt16(output, extendedLengths ? ushort.MaxValue : checked((ushort)keyLength));
        MidgeDiskFormat.WriteUInt32(output, extendedLengths ? uint.MaxValue : checked((uint)value.Length));
        MidgeDiskFormat.WriteUInt64(output, entry.Sequence);
        output.WriteByte(entry.IsDelete ? (byte)2 : (byte)0);
        output.WriteByte(entry.Expiration.HasValue ? (byte)1 : (byte)0);
        MidgeDiskFormat.WriteUInt64(output, entry.Expiration ?? 0);
        if (extendedLengths)
        {
            MidgeDiskFormat.WriteUInt32(output, checked((uint)keyLength));
            MidgeDiskFormat.WriteUInt32(output, checked((uint)value.Length));
        }

        output.Write(entry.Key, shared, keyLength);
        output.Write(value);
        return output.ToArray();
    }

    private static void DecodeEntries(byte[] block, List<MidgeSstEntry> entries)
    {
        var cursor = 0;
        byte[] previousKey = [];
        while (cursor < block.Length)
        {
            if (block.Length - cursor < EntryHeaderSize)
            {
                throw new PantsStorageException("SST data entry header is truncated.");
            }

            var shared = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(cursor, 2));
            ushort encodedKeyLength = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(cursor + 2, 2));
            uint encodedValueLength = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(cursor + 4, 4));
            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(cursor + 8, 8));
            var entryType = block[cursor + 16];
            var expirationPresent = block[cursor + 17];
            var expirationRaw = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(cursor + 18, 8));
            cursor += EntryHeaderSize;
            int keyLength;
            int valueLength;
            if (encodedKeyLength == ushort.MaxValue && encodedValueLength == uint.MaxValue)
            {
                if (block.Length - cursor < 2 * sizeof(uint))
                {
                    throw new PantsStorageException("SST extended entry lengths are truncated.");
                }

                keyLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(cursor)));
                valueLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(cursor + sizeof(uint))));
                cursor += 2 * sizeof(uint);
            }
            else
            {
                keyLength = encodedKeyLength;
                valueLength = checked((int)encodedValueLength);
            }

            if (shared > previousKey.Length ||
                keyLength > block.Length - cursor ||
                valueLength > block.Length - cursor - keyLength)
            {
                throw new PantsStorageException("SST data entry key or value is truncated.");
            }

            if (entryType is not (0 or 1 or 2 or 3) || expirationPresent > 1 || (expirationPresent == 0 && expirationRaw != 0))
            {
                throw new PantsStorageException("SST data entry metadata is invalid.");
            }

            var key = new byte[shared + keyLength];
            previousKey.AsSpan(0, shared).CopyTo(key);
            block.AsSpan(cursor, keyLength).CopyTo(key.AsSpan(shared));
            cursor += keyLength;
            var value = entryType == 2 ? null : block.AsSpan(cursor, valueLength).ToArray();
            cursor += valueLength;
            entries.Add(new MidgeSstEntry(key, value, sequence, expirationPresent == 1 ? expirationRaw : null, entryType == 2));
            previousKey = key;
        }
    }

    private static BlockHandle AppendBlock(Stream stream, byte[] raw, PantsPerformanceGoal performanceGoal)
    {
        (byte[] payload, MidgeCompressionAlgorithm algorithm) = CompressBlock(raw, performanceGoal);
        var offset = checked((ulong)stream.Position);
        var withTrailer = new byte[payload.Length + 5];
        payload.CopyTo(withTrailer, 0);
        withTrailer[payload.Length] = (byte)algorithm;
        BinaryPrimitives.WriteUInt32LittleEndian(
            withTrailer.AsSpan(payload.Length + 1),
            MidgeDiskFormat.Crc32C(withTrailer.AsSpan(0, payload.Length + 1)));
        MidgeDiskFormat.WriteUInt32(stream, (uint)withTrailer.Length);
        stream.Write(withTrailer);
        return new BlockHandle(offset, checked((ulong)withTrailer.Length + 4));
    }

    private static (byte[] Payload, MidgeCompressionAlgorithm Algorithm) CompressBlock(
        byte[] raw,
        PantsPerformanceGoal performanceGoal)
    {
        if (raw.Length < MinimumCompressionInputSize)
        {
            return (raw, MidgeCompressionAlgorithm.None);
        }

        return performanceGoal switch
        {
            PantsPerformanceGoal.Latency => CompressLz4(raw),
            PantsPerformanceGoal.Throughput => CompressAdaptive(raw),
            PantsPerformanceGoal.Economy => CompressZstd(raw, level: 9, MidgeCompressionAlgorithm.Zstd9),
            _ => throw new PantsInternalException($"Unknown performance goal '{performanceGoal}'.")
        };
    }

    private static (byte[] Payload, MidgeCompressionAlgorithm Algorithm) CompressAdaptive(byte[] raw)
    {
        (byte[] Payload, MidgeCompressionAlgorithm Algorithm)[] candidates =
        [
            CompressLz4(raw),
            CompressZstd(raw, level: 3, MidgeCompressionAlgorithm.Zstd3)
        ];

        (byte[] Payload, MidgeCompressionAlgorithm Algorithm)? best = null;
        foreach ((byte[] payload, MidgeCompressionAlgorithm algorithm) in candidates)
        {
            if (!CompressionQualifies(raw.Length, payload.Length) ||
                best is { } current && payload.Length >= current.Payload.Length)
            {
                continue;
            }

            best = (payload, algorithm);
        }

        return best ?? (raw, MidgeCompressionAlgorithm.None);
    }

    private static bool CompressionQualifies(int originalSize, int compressedSize)
    {
        if (compressedSize >= originalSize || originalSize - compressedSize < AdaptiveMinimumSavings)
        {
            return false;
        }

        double threshold = AdaptiveMaximumRatio;
        int thresholdBits = BitConverter.SingleToInt32Bits(AdaptiveMaximumRatio);
        float next = BitConverter.Int32BitsToSingle(checked(thresholdBits + 1));
        threshold += ((double)next - threshold) / 2D;
        return (double)compressedSize / originalSize <= threshold;
    }

    private static (byte[] Payload, MidgeCompressionAlgorithm Algorithm) CompressLz4(byte[] raw)
    {
        var compressed = new byte[checked(sizeof(uint) + LZ4Codec.MaximumOutputSize(raw.Length))];
        BinaryPrimitives.WriteUInt32LittleEndian(compressed, checked((uint)raw.Length));
        int encodedLength = LZ4Codec.Encode(raw, compressed.AsSpan(sizeof(uint)));
        if (encodedLength <= 0)
        {
            throw new PantsInternalException("LZ4 compression failed.");
        }

        Array.Resize(ref compressed, checked(sizeof(uint) + encodedLength));
        return (compressed, MidgeCompressionAlgorithm.Lz4);
    }

    private static (byte[] Payload, MidgeCompressionAlgorithm Algorithm) CompressZstd(
        byte[] raw,
        int level,
        MidgeCompressionAlgorithm algorithm)
    {
        using var compressor = new Compressor(level);
        return (compressor.Wrap(raw).ToArray(), algorithm);
    }

    private static byte[] ReadBlock(byte[] file, BlockHandle handle)
    {
        if (handle.Offset > (ulong)file.Length || handle.Size < 9 || handle.Size > (ulong)file.Length - handle.Offset)
        {
            throw new PantsStorageException("SST block handle is outside the file.");
        }

        var offset = checked((int)handle.Offset);
        var encodedLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset, 4)));
        if ((ulong)(encodedLength + 4) != handle.Size || encodedLength < 5)
        {
            throw new PantsStorageException("SST block length does not match its handle.");
        }

        var encoded = file.AsSpan(offset + 4, encodedLength);
        var algorithm = encoded[^5];
        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(encoded[^4..]);
        if (MidgeDiskFormat.Crc32C(encoded[..^4]) != storedCrc)
        {
            throw new PantsStorageException("SST block checksum mismatch.");
        }

        return MidgeDiskFormat.Decompress(encoded[..^5], algorithm);
    }

    private static byte[] EncodeMetadata(
        IReadOnlyList<MidgeSstEntry> entries,
        IReadOnlyList<MidgeRangeTombstone> tombstones,
        BlockHandle? rangeHandle,
        MidgeSstIndexKind indexKind)
    {
        var keys = entries.Select(entry => entry.Key)
            .Concat(tombstones.SelectMany(tombstone => new[] { tombstone.Start, tombstone.End }))
            .OrderBy(key => key, ByteArrayComparer.Instance)
            .ToList();
        using var output = new MemoryStream();
        MidgeDiskFormat.WriteUInt32(output, MidgeDiskFormat.SstFormatVersion);
        output.WriteByte((byte)indexKind);
        output.WriteByte(keys.Count > 0 ? (byte)1 : (byte)0);
        WriteUInt16(output, 0);
        WriteHandle(output, rangeHandle ?? default);
        if (keys.Count > 0)
        {
            WriteLengthPrefixed(output, keys[0]);
            WriteLengthPrefixed(output, keys[^1]);
        }

        return output.ToArray();
    }

    private static SstMetadata DecodeMetadata(byte[] metadata)
    {
        if (metadata.Length < 24)
        {
            throw new PantsStorageException("SST metadata block is truncated.");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(metadata);
        if (version != MidgeDiskFormat.SstFormatVersion)
        {
            throw new PantsStorageException($"Unsupported SST metadata format version '{version}'.");
        }

        byte rawIndexKind = metadata[4];
        byte flags = metadata[5];
        if (rawIndexKind is not (0 or 1) || (flags & ~1) != 0 || metadata[6] != 0 || metadata[7] != 0)
        {
            throw new PantsStorageException("SST metadata flags, index kind, or reserved bytes are invalid.");
        }

        BlockHandle rawRangeHandle = ReadHandle(metadata, 8);
        if (rawRangeHandle.Size == 0 && rawRangeHandle.Offset != 0)
        {
            throw new PantsStorageException("SST range-tombstone handle is only partially present.");
        }

        BlockHandle? rangeHandle = rawRangeHandle.Offset == 0 && rawRangeHandle.Size == 0
            ? null
            : rawRangeHandle;
        int cursor = 24;
        byte[]? smallestKey = null;
        byte[]? largestKey = null;
        if ((flags & 1) == 0)
        {
            if (cursor != metadata.Length)
            {
                throw new PantsStorageException("SST metadata without a key range has trailing bytes.");
            }
        }
        else
        {
            smallestKey = ReadLengthPrefixed(metadata, ref cursor);
            largestKey = ReadLengthPrefixed(metadata, ref cursor);
            if (cursor != metadata.Length ||
                ByteArrayComparer.Instance.Compare(smallestKey, largestKey) > 0)
            {
                throw new PantsStorageException("SST metadata key range is malformed or inverted.");
            }
        }

        return new SstMetadata((MidgeSstIndexKind)rawIndexKind, rangeHandle, smallestKey, largestKey);
    }

    private static void ValidateBlockBlooms(byte[] bytes, int expectedBlockCount)
    {
        if (bytes.Length < sizeof(uint))
        {
            throw new PantsStorageException("SST block-bloom header is truncated.");
        }

        int blockCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        int headerLength = checked(sizeof(uint) + (blockCount * sizeof(uint)));
        if (blockCount != expectedBlockCount || bytes.Length < headerLength)
        {
            throw new PantsStorageException("SST block-bloom count or offset table is invalid.");
        }

        ReadOnlySpan<byte> bloomData = bytes.AsSpan(headerLength);
        int previousOffset = -1;
        for (int index = 0; index < blockCount; index++)
        {
            int offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(sizeof(uint) + (index * sizeof(uint)))));
            if ((index == 0 && offset != 0) || offset <= previousOffset || offset > bloomData.Length)
            {
                throw new PantsStorageException("SST block-bloom offset table is invalid.");
            }

            previousOffset = offset;
        }

        for (int index = 0; index < blockCount; index++)
        {
            int start = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(sizeof(uint) + (index * sizeof(uint)))));
            int end = index + 1 == blockCount
                ? bloomData.Length
                : checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(sizeof(uint) + ((index + 1) * sizeof(uint)))));
            ValidateBloomFilter(bloomData[start..end]);
        }
    }

    private static void ValidateBloomFilter(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 9)
        {
            throw new PantsStorageException("SST bloom filter is truncated.");
        }

        uint numberOfBits = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        byte hashFunctions = bytes[8];
        long expectedBytes = checked(((long)numberOfBits + 7) / 8);
        if (numberOfBits == 0 || hashFunctions is < 1 or > 8 || bytes.Length - 9 != expectedBytes)
        {
            throw new PantsStorageException("SST bloom filter parameters are invalid.");
        }
    }

    private static void ValidateBlockCoverage(
        int footerOffset,
        BlockHandle metadata,
        BlockHandle index,
        BlockHandle? trie,
        BlockHandle? bloom,
        BlockHandle? range,
        List<(byte[] FirstKey, BlockHandle Handle)> dataBlocks)
    {
        var handles = new List<BlockHandle>(checked(2 + dataBlocks.Count + 3))
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
            throw new PantsStorageException("SST block references leave unreferenced leading bytes.");
        }

        for (int position = 1; position < handles.Count; position++)
        {
            ulong previousEnd = checked(handles[position - 1].Offset + handles[position - 1].Size);
            if (previousEnd != handles[position].Offset)
            {
                throw new PantsStorageException(
                    previousEnd > handles[position].Offset
                        ? "SST block references overlap."
                        : "SST block references leave unreferenced bytes.");
            }
        }

        BlockHandle last = handles[^1];
        if (checked(last.Offset + last.Size) != checked((ulong)footerOffset))
        {
            throw new PantsStorageException("SST block references do not exactly reach the footer.");
        }
    }

    private static void ValidateEntryOrdering(List<MidgeSstEntry> entries)
    {
        for (int index = 1; index < entries.Count; index++)
        {
            int comparison = ByteArrayComparer.Instance.Compare(entries[index - 1].Key, entries[index].Key);
            if (comparison > 0 ||
                (comparison == 0 && entries[index - 1].Sequence < entries[index].Sequence))
            {
                throw new PantsStorageException("SST data entries are not in canonical key and sequence order.");
            }
        }
    }

    private static void ValidateMetadataKeyRange(
        SstMetadata metadata,
        List<MidgeSstEntry> entries,
        List<MidgeRangeTombstone> tombstones)
    {
        byte[][] keys = entries.Select(static entry => entry.Key)
            .Concat(tombstones.SelectMany(static tombstone => new[] { tombstone.Start, tombstone.End }))
            .OrderBy(static key => key, ByteArrayComparer.Instance)
            .ToArray();
        if (keys.Length == 0)
        {
            if (metadata.SmallestKey is not null || metadata.LargestKey is not null)
            {
                throw new PantsStorageException("Empty SST metadata unexpectedly declares a key range.");
            }

            return;
        }

        if (metadata.SmallestKey is null ||
            metadata.LargestKey is null ||
            !metadata.SmallestKey.AsSpan().SequenceEqual(keys[0]) ||
            !metadata.LargestKey.AsSpan().SequenceEqual(keys[^1]))
        {
            throw new PantsStorageException("SST metadata key range does not match its contents.");
        }
    }

    private static byte[] EncodeIndex(IEnumerable<(byte[] FirstKey, BlockHandle Handle)> entries)
    {
        using var output = new MemoryStream();
        foreach (var (key, handle) in entries)
        {
            WriteLengthPrefixed(output, key);
            WriteHandle(output, handle);
        }

        return output.ToArray();
    }

    private static List<(byte[] FirstKey, BlockHandle Handle)> DecodeIndex(byte[] bytes)
    {
        var output = new List<(byte[], BlockHandle)>();
        var cursor = 0;
        while (cursor < bytes.Length)
        {
            if (bytes.Length - cursor < 4)
            {
                throw new PantsStorageException("SST index key length is truncated.");
            }

            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4)));
            cursor += 4;
            if (length > bytes.Length - cursor - 16)
            {
                throw new PantsStorageException("SST index entry is truncated.");
            }

            var key = bytes.AsSpan(cursor, length).ToArray();
            cursor += length;
            var handle = ReadHandle(bytes, cursor);
            cursor += 16;
            output.Add((key, handle));
        }

        return output;
    }

    private static MidgeTrieIndex? DecodeTrieIndex(
        byte[] file,
        MidgeSstIndexKind indexKind,
        BlockHandle? trieHandle,
        IReadOnlyList<(byte[] FirstKey, BlockHandle Handle)> blockIndex)
    {
        return (indexKind, trieHandle) switch
        {
            (MidgeSstIndexKind.Trie, { } handle) => MidgeTrieIndex.Decode(
                ReadBlock(file, handle),
                blockIndex.Select(static entry => entry.FirstKey).ToArray()),
            (MidgeSstIndexKind.Trie, null) => throw new PantsStorageException(
                "Trie-selected SST metadata is missing its trie footer handle."),
            (MidgeSstIndexKind.Sparse, not null) => throw new PantsStorageException(
                "Sparse-selected SST metadata unexpectedly carries a trie footer handle."),
            (MidgeSstIndexKind.Sparse, null) => null,
            _ => throw new PantsStorageException("SST metadata selects an unsupported index kind.")
        };
    }

    private static int FindFloorBlock(
        IReadOnlyList<(byte[] FirstKey, BlockHandle Handle)> index,
        ReadOnlySpan<byte> key)
    {
        var low = 0;
        int high = index.Count - 1;
        var result = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
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

    private static byte[] EncodeBlockBlooms(IReadOnlyList<IReadOnlyList<byte[]>> blockKeys)
    {
        byte[][] blooms = blockKeys.Select(EncodeBloom).ToArray();
        using var output = new MemoryStream();
        MidgeDiskFormat.WriteUInt32(output, checked((uint)blooms.Length));
        uint offset = 0;
        foreach (byte[] bloom in blooms)
        {
            MidgeDiskFormat.WriteUInt32(output, offset);
            offset = checked(offset + (uint)bloom.Length);
        }

        foreach (byte[] bloom in blooms)
        {
            output.Write(bloom);
        }

        return output.ToArray();
    }

    private static byte[] EncodeBloom(IReadOnlyList<byte[]> keys)
    {
        int estimatedKeys = Math.Max(1, keys.Count);
        int numberOfBits = Math.Max(
            64,
            checked((int)Math.Ceiling(
                -estimatedKeys * Math.Log(0.01) / Math.Pow(Math.Log(2), 2))));
        byte hashFunctions = checked((byte)Math.Clamp(
            (int)Math.Round(
                ((double)numberOfBits / estimatedKeys) * Math.Log(2),
                MidpointRounding.AwayFromZero),
            1,
            8));
        byte[] bits = new byte[(numberOfBits + 7) / 8];
        foreach (byte[] key in keys)
        {
            SetBloomBits(bits, numberOfBits, hashFunctions, key);
        }

        using var output = new MemoryStream(checked(9 + bits.Length));
        MidgeDiskFormat.WriteUInt32(output, checked((uint)numberOfBits));
        MidgeDiskFormat.WriteUInt32(output, checked((uint)keys.Count));
        output.WriteByte(hashFunctions);
        output.Write(bits);
        return output.ToArray();
    }

    private static bool BloomMightContain(
        ReadOnlySpan<byte> serializedBlooms,
        int blockIndex,
        ReadOnlySpan<byte> key)
    {
        int blockCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(serializedBlooms));
        if (blockIndex >= blockCount)
        {
            return true;
        }

        int headerLength = checked(sizeof(uint) + (blockCount * sizeof(uint)));
        int start = checked(headerLength + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            serializedBlooms.Slice(sizeof(uint) + (blockIndex * sizeof(uint)), sizeof(uint))));
        int end = blockIndex + 1 == blockCount
            ? serializedBlooms.Length
            : checked(headerLength + (int)BinaryPrimitives.ReadUInt32LittleEndian(
                serializedBlooms.Slice(sizeof(uint) + ((blockIndex + 1) * sizeof(uint)), sizeof(uint))));
        ReadOnlySpan<byte> bloom = serializedBlooms[start..end];
        ValidateBloomFilter(bloom);
        int numberOfBits = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bloom));
        byte hashFunctions = bloom[8];
        ReadOnlySpan<byte> bits = bloom[9..];
        ulong firstHash = Hash(key, BloomSeedOne);
        ulong secondHash = Hash(key, BloomSeedTwo);
        for (ulong hashIndex = 0; hashIndex < hashFunctions; hashIndex++)
        {
            ulong bitIndex = unchecked(firstHash + (hashIndex * secondHash)) % checked((ulong)numberOfBits);
            if ((bits[checked((int)(bitIndex / 8))] & (1 << checked((int)(bitIndex % 8)))) == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void SetBloomBits(
        Span<byte> bits,
        int numberOfBits,
        byte hashFunctions,
        ReadOnlySpan<byte> key)
    {
        ulong firstHash = Hash(key, BloomSeedOne);
        ulong secondHash = Hash(key, BloomSeedTwo);
        for (ulong hashIndex = 0; hashIndex < hashFunctions; hashIndex++)
        {
            ulong bitIndex = unchecked(firstHash + (hashIndex * secondHash)) % checked((ulong)numberOfBits);
            bits[checked((int)(bitIndex / 8))] |= checked((byte)(1 << checked((int)(bitIndex % 8))));
        }
    }

    private static ulong Hash(ReadOnlySpan<byte> key, ulong seed)
    {
        var hasher = new XxHash3(unchecked((long)seed));
        hasher.Append(key);
        return hasher.GetCurrentHashAsUInt64();
    }

    private static byte[] EncodeRangeTombstones(IReadOnlyList<MidgeRangeTombstone> tombstones)
    {
        using var output = new MemoryStream();
        MidgeDiskFormat.WriteUInt32(output, (uint)tombstones.Count);
        foreach (var tombstone in tombstones)
        {
            WriteLengthPrefixed(output, tombstone.Start);
            WriteLengthPrefixed(output, tombstone.End);
            MidgeDiskFormat.WriteUInt64(output, tombstone.Sequence);
        }

        return output.ToArray();
    }

    private static List<MidgeRangeTombstone> DecodeRangeTombstones(byte[] bytes)
    {
        if (bytes.Length < 4)
        {
            throw new PantsStorageException("SST range tombstone block is truncated.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var cursor = 4;
        var output = new List<MidgeRangeTombstone>(checked((int)count));
        for (var index = 0; index < count; index++)
        {
            var start = ReadLengthPrefixed(bytes, ref cursor);
            var end = ReadLengthPrefixed(bytes, ref cursor);
            if (bytes.Length - cursor < 8)
            {
                throw new PantsStorageException("SST range tombstone sequence is truncated.");
            }

            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(cursor, 8));
            cursor += 8;
            output.Add(new MidgeRangeTombstone(start, end, sequence));
        }

        if (cursor != bytes.Length)
        {
            throw new PantsStorageException("SST range tombstone block has trailing bytes.");
        }

        return output;
    }

    private static byte[] EncodeFooter(
        BlockHandle meta,
        BlockHandle index,
        BlockHandle? trie,
        BlockHandle bloom)
    {
        var footer = new byte[MidgeDiskFormat.SstFooterSize];
        WriteHandle(footer, 0, meta);
        WriteHandle(footer, 16, index);
        if (trie.HasValue)
        {
            WriteHandle(footer, 32, trie.Value);
        }

        WriteHandle(footer, 48, bloom);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(64), MidgeDiskFormat.SstFormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(68), MidgeDiskFormat.SstFooterSize);
        BinaryPrimitives.WriteUInt64LittleEndian(footer.AsSpan(72), MidgeDiskFormat.SstFooterMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(80), MidgeDiskFormat.Crc32C(footer.AsSpan(0, 80)));
        return footer;
    }

    private static BlockHandle ReadHandle(ReadOnlySpan<byte> bytes, int offset) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8)),
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset + 8, 8)));

    private static BlockHandle? ReadOptionalHandle(
        ReadOnlySpan<byte> bytes,
        int offset,
        string description)
    {
        BlockHandle handle = ReadHandle(bytes, offset);
        if (handle.Offset == 0 && handle.Size == 0)
        {
            return null;
        }

        if (handle.Size == 0)
        {
            throw new PantsStorageException($"SST footer {description} handle is only partially present.");
        }

        return handle;
    }

    private static void WriteHandle(Stream stream, BlockHandle handle)
    {
        MidgeDiskFormat.WriteUInt64(stream, handle.Offset);
        MidgeDiskFormat.WriteUInt64(stream, handle.Size);
    }

    private static void WriteHandle(Span<byte> bytes, int offset, BlockHandle handle)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(offset, 8), handle.Offset);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(offset + 8, 8), handle.Size);
    }

    private static void WriteLengthPrefixed(Stream stream, byte[] bytes)
    {
        MidgeDiskFormat.WriteUInt32(stream, (uint)bytes.Length);
        stream.Write(bytes);
    }

    private static byte[] ReadLengthPrefixed(byte[] bytes, ref int cursor)
    {
        if (bytes.Length - cursor < 4)
        {
            throw new PantsStorageException("SST length-prefixed field is truncated.");
        }

        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4)));
        cursor += 4;
        if (length > bytes.Length - cursor)
        {
            throw new PantsStorageException("SST length-prefixed value is truncated.");
        }

        var value = bytes.AsSpan(cursor, length).ToArray();
        cursor += length;
        return value;
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private readonly record struct BlockHandle(ulong Offset, ulong Size);

    private sealed record SstMetadata(
        MidgeSstIndexKind IndexKind,
        BlockHandle? RangeHandle,
        byte[]? SmallestKey,
        byte[]? LargestKey);
}
