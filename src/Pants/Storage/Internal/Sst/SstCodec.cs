using System.Buffers.Binary;
using System.IO.Hashing;
using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants.Storage.Internal.Sst;

static class SstCodec
{
    const int TargetBlockSize = 64 * 1024;
    const int EntryHeaderSize = 26;
    const ulong BloomSeedOne = 0x9E37_79B1_85EB_CA87;
    const ulong BloomSeedTwo = 0xC2B2_AE3D_27D4_EB4F;

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

        var entries = sourceEntries
            .OrderBy(entry => entry.Key, ByteArrayComparer.Instance)
            .ThenByDescending(entry => entry.Sequence)
            .ToList();
        var index = new List<(byte[] FirstKey, SstBlockHandle Handle)>();
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

        // Block blooms are written before the range-tombstone block so that a present
        // range-tombstone handle can never legitimately land at file offset 0 - that
        // offset is reserved to disambiguate an absent (offset 0, size 0) handle from a
        // partially-present one (see ValidateFooter / DecodeMetadata's symmetric check).
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
            throw new StorageException("SST is shorter than its V4 footer.");
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
            var firstEntryIndex = entries.Count;
            DecodeEntries(ReadBlock(bytes, handle), entries);
            if (entries.Count == firstEntryIndex ||
                !entries[firstEntryIndex].Key.AsSpan().SequenceEqual(firstKey))
            {
                throw new StorageException("SST index first key does not match its data block.");
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
            throw new StorageException("SST is shorter than its V4 footer.");
        }

        ReadOnlySpan<byte> footer = bytes.AsSpan(bytes.Length - DiskFormat.SstFooterSize);
        var index = DecodeIndex(
            ReadBlock(bytes, ReadHandle(footer, 16)));
        var metadata = DecodeMetadata(ReadBlock(bytes, ReadHandle(footer, 0)));
        var trieIndex = DecodeTrieIndex(
            bytes,
            metadata.IndexKind,
            ReadOptionalHandle(footer, 32, "trie"),
            index);
        var bloomHandle = ReadOptionalHandle(footer, 48, "block bloom");
        if (index.Count == 0 || bloomHandle is null)
        {
            return new SstPointReadDecision(0, 0, 0, false, -1, 0);
        }

        var trieCandidate = trieIndex?.FindFloorBlock(key) ?? -1;
        var candidate = trieCandidate >= 0 ? trieCandidate : FindFloorBlock(index, key);

        if (candidate < 0)
        {
            return new SstPointReadDecision(0, 0, 0, false, -1, 0);
        }

        var mightContain = BloomMightContain(
            ReadBlock(bytes, bloomHandle.Value),
            candidate,
            key);
        var candidateHandle = index[candidate].Handle;
        var blockSizeBytes = DecodeInt32(candidateHandle.Size, "block size");
        return mightContain
            ? new SstPointReadDecision(1, 1, 1, false, candidate, blockSizeBytes)
            : new SstPointReadDecision(1, 1, 0, true, candidate, blockSizeBytes);
    }

    internal static SstIndexKind GetIndexKind(byte[] bytes)
    {
        if (bytes.Length < DiskFormat.SstFooterSize)
        {
            throw new StorageException("SST is shorter than its V4 footer.");
        }

        ReadOnlySpan<byte> footer = bytes.AsSpan(bytes.Length - DiskFormat.SstFooterSize);
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
        DecodeEntries(block, entries);
        foreach (var entry in entries)
        {
            if (entry.Key.AsSpan().SequenceEqual(key))
            {
                return true;
            }
        }

        return false;
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
        var extendedLengths = keyLength > ushort.MaxValue;
        using var output = new MemoryStream(checked(
            EntryHeaderSize +
            (extendedLengths ? 2 * sizeof(uint) : 0) +
            keyLength +
            value.Length));
        WriteUInt16(output, (ushort)shared);
        WriteUInt16(output, extendedLengths ? ushort.MaxValue : checked((ushort)keyLength));
        DiskFormat.WriteUInt32(output, extendedLengths ? uint.MaxValue : checked((uint)value.Length));
        DiskFormat.WriteUInt64(output, entry.Sequence);
        output.WriteByte(entry.IsDelete ? (byte)2 : (byte)0);
        output.WriteByte(entry.Expiration.HasValue ? (byte)1 : (byte)0);
        DiskFormat.WriteUInt64(output, entry.Expiration ?? 0);
        if (extendedLengths)
        {
            DiskFormat.WriteUInt32(output, checked((uint)keyLength));
            DiskFormat.WriteUInt32(output, checked((uint)value.Length));
        }

        output.Write(entry.Key, shared, keyLength);
        output.Write(value);
        return output.ToArray();
    }

    static void DecodeEntries(byte[] block, List<SstEntry> entries)
    {
        var cursor = 0;
        byte[] previousKey = [];
        while (cursor < block.Length)
        {
            if (block.Length - cursor < EntryHeaderSize)
            {
                throw new StorageException("SST data entry header is truncated.");
            }

            var shared = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(cursor, 2));
            var encodedKeyLength = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(cursor + 2, 2));
            var encodedValueLength = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(cursor + 4, 4));
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
                    throw new StorageException("SST extended entry lengths are truncated.");
                }

                keyLength = DecodeInt32(
                    BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(cursor)),
                    "extended key length");
                valueLength =
                    DecodeInt32(
                        BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(cursor + sizeof(uint))),
                        "extended value length");
                cursor += 2 * sizeof(uint);
            }
            else
            {
                keyLength = encodedKeyLength;
                valueLength = DecodeInt32(encodedValueLength, "value length");
            }

            if (shared > previousKey.Length ||
                keyLength > block.Length - cursor ||
                valueLength > block.Length - cursor - keyLength)
            {
                throw new StorageException("SST data entry key or value is truncated.");
            }

            if (entryType is not (0 or 1 or 2 or 3) || expirationPresent > 1 ||
                (expirationPresent == 0 && expirationRaw != 0))
            {
                throw new StorageException("SST data entry metadata is invalid.");
            }

            var key = new byte[shared + keyLength];
            previousKey.AsSpan(0, shared).CopyTo(key);
            block.AsSpan(cursor, keyLength).CopyTo(key.AsSpan(shared));
            cursor += keyLength;
            var value = entryType == 2 && valueLength == 0
                ? null
                : block.AsSpan(cursor, valueLength).ToArray();
            cursor += valueLength;
            entries.Add(new SstEntry(key, value, sequence, expirationPresent == 1 ? expirationRaw : null,
                entryType == 2));
            previousKey = key;
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
            throw new StorageException("SST block handle is outside the file.");
        }

        var offset = DecodeInt32(handle.Offset, "block offset");
        var encodedLength = DecodeInt32(
            BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset, 4)),
            "encoded block length");
        if ((ulong)encodedLength + 4 != handle.Size || encodedLength < 5)
        {
            throw new StorageException("SST block length does not match its handle.");
        }

        var encoded = file.AsSpan(offset + 4, encodedLength);
        try
        {
            return SstBlockCodec.DecompressWithTrailer(encoded);
        }
        catch (PantsCorruptionException exception)
        {
            throw new StorageException(exception.Message, exception);
        }
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
            throw new StorageException("SST block handle is outside the file.");
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
        var keys = entries.Select(entry => entry.Key)
            .Concat(tombstones.SelectMany(tombstone => new[] { tombstone.Start, tombstone.End }))
            .OrderBy(key => key, ByteArrayComparer.Instance)
            .ToList();
        using var output = new MemoryStream();
        DiskFormat.WriteUInt32(output, DiskFormat.SstFormatVersion);
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

    internal static SstMetadata DecodeMetadata(byte[] metadata)
    {
        if (metadata.Length < 24)
        {
            throw new StorageException("SST metadata block is truncated.");
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
            throw new StorageException("SST metadata flags, index kind, or reserved bytes are invalid.");
        }

        var rawRangeHandle = ReadHandle(metadata, 8);
        if ((rawRangeHandle.Offset == 0) != (rawRangeHandle.Size == 0))
        {
            throw new PantsCorruptionException("SST range-tombstone handle is only partially present.");
        }

        SstBlockHandle? rangeHandle = rawRangeHandle.Offset == 0 && rawRangeHandle.Size == 0
            ? null
            : rawRangeHandle;
        var cursor = 24;
        byte[]? smallestKey = null;
        byte[]? largestKey = null;
        if ((flags & 1) == 0)
        {
            if (cursor != metadata.Length)
            {
                throw new StorageException("SST metadata without a key range has trailing bytes.");
            }
        }
        else
        {
            smallestKey = ReadLengthPrefixed(metadata, ref cursor);
            largestKey = ReadLengthPrefixed(metadata, ref cursor);
            if (cursor != metadata.Length ||
                ByteArrayComparer.Instance.Compare(smallestKey, largestKey) > 0)
            {
                throw new StorageException("SST metadata key range is malformed or inverted.");
            }
        }

        return new SstMetadata((SstIndexKind)rawIndexKind, rangeHandle, smallestKey, largestKey);
    }

    internal static void ValidateBlockBlooms(byte[] bytes, int expectedBlockCount)
    {
        if (bytes.Length < sizeof(uint))
        {
            throw new StorageException("SST block-bloom header is truncated.");
        }

        var blockCount = DecodeInt32(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            "block-bloom count");
        if (blockCount != expectedBlockCount || blockCount > (bytes.Length - sizeof(uint)) / sizeof(uint))
        {
            throw new StorageException("SST block-bloom count or offset table is invalid.");
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
                throw new StorageException("SST block-bloom offset table is invalid.");
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
            throw new StorageException("SST bloom filter is truncated.");
        }

        var numberOfBits = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var hashFunctions = bytes[8];
        var expectedBytes = checked(((long)numberOfBits + 7) / 8);
        if (numberOfBits == 0 || hashFunctions is < 1 or > 8 || bytes.Length - 9 != expectedBytes)
        {
            throw new StorageException("SST bloom filter parameters are invalid.");
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
            throw new StorageException("SST block references leave unreferenced leading bytes.");
        }

        for (var position = 1; position < handles.Count; position++)
        {
            var previousEnd = checked(handles[position - 1].Offset + handles[position - 1].Size);
            if (previousEnd != handles[position].Offset)
            {
                throw new StorageException(
                    previousEnd > handles[position].Offset
                        ? "SST block references overlap."
                        : "SST block references leave unreferenced bytes.");
            }
        }

        var last = handles[^1];
        if (checked(last.Offset + last.Size) != checked((ulong)footerOffset))
        {
            throw new StorageException("SST block references do not exactly reach the footer.");
        }
    }

    static void ValidateEntryOrdering(List<SstEntry> entries)
    {
        for (var index = 1; index < entries.Count; index++)
        {
            var comparison = ByteArrayComparer.Instance.Compare(entries[index - 1].Key, entries[index].Key);
            if (comparison > 0 ||
                (comparison == 0 && entries[index - 1].Sequence < entries[index].Sequence))
            {
                throw new StorageException("SST data entries are not in canonical key and sequence order.");
            }
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
                throw new StorageException("Empty SST metadata unexpectedly declares a key range.");
            }

            return;
        }

        if (metadata.SmallestKey is null ||
            metadata.LargestKey is null ||
            !metadata.SmallestKey.AsSpan().SequenceEqual(keys[0]) ||
            !metadata.LargestKey.AsSpan().SequenceEqual(keys[^1]))
        {
            throw new StorageException("SST metadata key range does not match its contents.");
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
                throw new StorageException("SST index key length is truncated.");
            }

            var length = DecodeInt32(
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4)),
                "index key length");
            cursor += 4;
            if (length > bytes.Length - cursor - 16)
            {
                throw new StorageException("SST index entry is truncated.");
            }

            var key = bytes.AsSpan(cursor, length).ToArray();
            cursor += length;
            var handle = ReadHandle(bytes, cursor);
            cursor += 16;
            if (output.Count > 0 && output[^1].Item1.AsSpan().SequenceCompareTo(key) > 0)
            {
                throw new StorageException("SST index first keys are not sorted in ascending order.");
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
        IReadOnlyList<(byte[] FirstKey, SstBlockHandle Handle)> blockIndex)
    {
        return (indexKind, trie) switch
        {
            (SstIndexKind.Trie, { } bytes) => TrieIndex.Decode(
                bytes,
                blockIndex.Select(static entry => entry.FirstKey).ToArray()),
            (SstIndexKind.Trie, null) => throw new StorageException(
                "Trie-selected SST metadata is missing its trie footer handle."),
            (SstIndexKind.Sparse, not null) => throw new StorageException(
                "Sparse-selected SST metadata unexpectedly carries a trie footer handle."),
            (SstIndexKind.Sparse, null) => null,
            _ => throw new StorageException("SST metadata selects an unsupported index kind.")
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
            throw new StorageException("SST block-bloom header is truncated.");
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
            throw new StorageException("SST block-bloom count or offset table is invalid.");
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
            throw new StorageException("SST block-bloom offset table is invalid.");
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
            throw new StorageException("SST range tombstone block is truncated.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var cursor = 4;
        var output = new List<RangeTombstone>(DecodeInt32(count, "range-tombstone count"));
        for (var index = 0; index < count; index++)
        {
            var start = ReadLengthPrefixed(bytes, ref cursor);
            var end = ReadLengthPrefixed(bytes, ref cursor);
            if (bytes.Length - cursor < 8)
            {
                throw new StorageException("SST range tombstone sequence is truncated.");
            }

            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(cursor, 8));
            cursor += 8;
            output.Add(new RangeTombstone(start, end, sequence));
        }

        if (cursor != bytes.Length)
        {
            throw new StorageException("SST range tombstone block has trailing bytes.");
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
            throw new PantsCorruptionException("SST V4 footer size is invalid.");
        }

        var magic = BinaryPrimitives.ReadUInt64LittleEndian(footer[72..80]);
        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer[80..84]);
        if (DiskFormat.Crc32C(footer[..80]) != storedCrc)
        {
            if (magic == DiskFormat.SstFooterMagic)
            {
                throw new PantsCompatibilityException(
                    "SST V4 footer checksum mismatch, but the footer matches a legacy magic value.");
            }

            throw new PantsCorruptionException("SST V4 footer checksum mismatch.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(footer[68..72]) != DiskFormat.SstFooterSize ||
            magic != DiskFormat.SstFooterMagic)
        {
            throw new PantsCorruptionException("SST V4 footer format or magic is invalid.");
        }

        var formatVersion = BinaryPrimitives.ReadUInt32LittleEndian(footer[64..68]);
        if (formatVersion != DiskFormat.SstFormatVersion)
        {
            throw new PantsCompatibilityException(
                $"Unsupported SST V4 footer format version '{formatVersion}'.");
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
            throw new StorageException($"SST footer {description} handle is only partially present.");
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
            throw new StorageException("SST length-prefixed field is truncated.");
        }

        var length = DecodeInt32(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4)),
            "length-prefixed field size");
        cursor += 4;
        if (length > bytes.Length - cursor)
        {
            throw new StorageException("SST length-prefixed value is truncated.");
        }

        var value = bytes.AsSpan(cursor, length).ToArray();
        cursor += length;
        return value;
    }

    static int AddBloomOffset(int headerLength, int offset, int totalLength)
    {
        if (offset > totalLength - headerLength)
        {
            throw new StorageException("SST block-bloom offset table is invalid.");
        }

        return headerLength + offset;
    }

    static int DecodeInt32(uint value, string field)
    {
        if (value > int.MaxValue)
        {
            throw new StorageException($"SST {field} exceeds the supported size.");
        }

        return (int)value;
    }

    static int DecodeInt32(ulong value, string field)
    {
        if (value > int.MaxValue)
        {
            throw new StorageException($"SST {field} exceeds the supported size.");
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
