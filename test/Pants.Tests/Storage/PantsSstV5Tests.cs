using System.Buffers.Binary;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsSstV5Tests
{
    [Fact]
    public void ShouldReadUnmodifiedMidgeV5FixtureAfterReaderReopen()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Compatibility", "LsmSpec", "7748fd1",
            "conformance", "fixtures", "format9", "populated-local", "sst",
            "000000_00_00000000000000000001.sst");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var reader = SstReader.Open(path);
            var entry = Assert.Single(SstCodec.DecodeDataBlock(reader.ReadDataBlock(0)),
                entry => entry.Key.AsSpan().SequenceEqual("fixture/alpha"u8));
            Assert.Equal("fixture/alpha"u8.ToArray(), entry.Key);
            Assert.Equal("value-alpha"u8.ToArray(), entry.Value);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)]
    public void ShouldRoundTripCompactEntryGivenOptionalExpiration(ulong? expiration)
    {
        var entry = new SstEntry("k"u8.ToArray(), "v"u8.ToArray(), ulong.MaxValue, expiration, false);
        var bytes = SstCodec.Encode([entry], [], PantsPerformanceGoal.Latency);
        var block = SstCodec.ReadBlock(bytes, new SstBlockHandle(0,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) + 4UL));

        Assert.Equal(12 + 2 + 8 + (expiration.HasValue ? 8 : 0), block.Length);
        var decoded = Assert.Single(SstCodec.DecodeDataBlock(block));
        Assert.Equal(entry.Key, decoded.Key);
        Assert.Equal(entry.Value, decoded.Value);
        Assert.Equal(entry.Sequence, decoded.Sequence);
        Assert.Equal(expiration, decoded.Expiration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldRejectRestartContinuationGivenMismatchWithAdjacentKey(bool sameKey)
    {
        var entries = Enumerable.Range(0, 32).Select(index => new SstEntry(
            sameKey ? "key"u8.ToArray() : TestBytes.FromString($"key/{index:D3}"),
            "value"u8.ToArray(), (ulong)(32 - index), null, false)).ToArray();
        var bytes = SstCodec.Encode(entries, [], PantsPerformanceGoal.Latency);
        var block = SstCodec.ReadBlock(bytes, new SstBlockHandle(0,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) + 4UL));
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(block.Length - 4)));
        var restart = block.AsSpan(block.Length - 8, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(restart,
            BinaryPrimitives.ReadUInt32LittleEndian(restart) ^ 0x8000_0000U);

        Assert.Throws<PantsCorruptionException>(() => SstCodec.DecodeDataBlock(block));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ShouldRejectMalformedCompactVarint(int corruption)
    {
        byte[] malformed = corruption switch
        {
            0 => [0x80, 0],
            1 => [0xff, 0xff, 0xff, 0xff, 0x10],
            2 => [0x80, 0x80, 0x80, 0x80, 0x80],
            3 => [0x80],
            _ => [0x80, 0x80, 4]
        };
        var block = WithRestarts(malformed, [0]);

        Assert.Throws<PantsCorruptionException>(() => SstCodec.DecodeDataBlock(block));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ShouldRejectMalformedRestartDirectory(int corruption)
    {
        var block = WithRestarts(Entry(0, 1, "k"u8.ToArray(), "v"u8.ToArray()), [0]);
        if (corruption == 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(block.Length - 4), 0);
        }
        else if (corruption == 1)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(block.Length - 8), 0x8000_0000);
        }
        else if (corruption == 2)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(block.Length - 8), 1);
        }
        else
        {
            block = WithRestarts(Entry(0, 1, "k"u8.ToArray(), "v"u8.ToArray()), [0, 4]);
        }

        Assert.Throws<PantsCorruptionException>(() => SstCodec.DecodeDataBlock(block));
    }

    [Fact]
    public void ShouldRejectMergeOperandAsIncompatible()
    {
        var block = WithRestarts(Entry(3, 1, "k"u8.ToArray(), "operand"u8.ToArray()), [0]);

        Assert.Throws<PantsCompatibilityException>(() => SstCodec.DecodeDataBlock(block));
    }

    [Fact]
    public void ShouldRejectValueBearingDeleteAsCorrupt()
    {
        var block = WithRestarts(Entry(2, 1, "k"u8.ToArray(), "v"u8.ToArray()), [0]);

        Assert.Throws<PantsCorruptionException>(() => SstCodec.DecodeDataBlock(block));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ShouldRejectConflictingEqualSequenceEntries(int conflict)
    {
        var first = new SstEntry("key"u8.ToArray(), "first"u8.ToArray(), 1, null, false);
        var duplicate = conflict switch
        {
            0 => first with { Value = "other"u8.ToArray() },
            1 => first with { EntryType = 1 },
            _ => first with { Expiration = 0 }
        };
        var bytes = SstCodec.Encode([first, duplicate], [], PantsPerformanceGoal.Latency);

        Assert.Throws<PantsCorruptionException>(() => SstCodec.Decode(bytes));
    }

    [Theory]
    [InlineData(PantsScanDirection.Forward)]
    [InlineData(PantsScanDirection.Reverse)]
    public void ShouldRejectConflictingEqualSequenceAcrossDataBlocks(PantsScanDirection direction)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "duplicate.sst");
        var first = new SstEntry("key"u8.ToArray(), new byte[40_000], 1, null, false);
        var second = first with { Value = Enumerable.Repeat((byte)1, 40_000).ToArray() };
        File.WriteAllBytes(path, SstCodec.Encode([first, second], [], PantsPerformanceGoal.Latency));
        using var reader = SstReader.Open(path);
        using var iterator = SstBlockIterator.Create(reader, direction);
        Assert.True(iterator.MoveNext());

        Assert.Throws<PantsCorruptionException>(() => iterator.MoveNext());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReadPointGivenOptionalBloomBlockIsAbsent(bool asynchronous)
    {
        using var directory = new TemporaryDirectory();
        using var store = LocalDiskStore.Open(directory.Path,
            new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry()));
        var original = SstCodec.Encode(
            [new SstEntry("key"u8.ToArray(), "value"u8.ToArray(), 1, null, false)],
            [], PantsPerformanceGoal.Latency);
        var bytes = RemoveBloomBlock(original);
        const string name = "000000_00_00000000000000000001.sst";
        File.WriteAllBytes(Path.Combine(directory.Path, "sst", name), bytes);
        var file = new FileMeta { Name = name, SizeBytes = (ulong)bytes.Length };

        var entry = asynchronous
            ? await store.TryReadPointValueAsync([file], "key"u8.ToArray(), CancellationToken.None)
            : store.TryReadPointValue([file], "key"u8);

        Assert.NotNull(entry);
        Assert.Equal("value"u8.ToArray(), entry.Value);
        Assert.Equal(0, SstCodec.GetPointReadDecision(bytes, "key"u8).BloomChecks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectConflictingOlderEqualSequencesAcrossBlocksOnPointRead(bool asynchronous)
    {
        using var directory = new TemporaryDirectory();
        using var store = LocalDiskStore.Open(directory.Path,
            new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry()));
        var first = new SstEntry("key"u8.ToArray(), new byte[40_000], 2, null, false);
        var older = first with { Sequence = 1 };
        var conflicting = older with { Value = Enumerable.Repeat((byte)1, 40_000).ToArray() };
        var bytes = SstCodec.Encode([first, older, conflicting], [], PantsPerformanceGoal.Latency);
        const string name = "000000_00_00000000000000000001.sst";
        File.WriteAllBytes(Path.Combine(directory.Path, "sst", name), bytes);
        var file = new FileMeta { Name = name, SizeBytes = (ulong)bytes.Length };

        if (asynchronous)
        {
            await Assert.ThrowsAsync<PantsCorruptionException>(() => store.TryReadPointValueAsync(
                [file], "key"u8.ToArray(), CancellationToken.None).AsTask());
        }
        else
        {
            Assert.Throws<PantsCorruptionException>(() => store.TryReadPointValue([file], "key"u8));
        }
    }

    [Fact]
    public async Task ShouldRejectOversizedEncodedDataBlockBeforeRequestingItsBody()
    {
        var bytes = SstCodec.Encode(
            [new SstEntry("key"u8.ToArray(), "value"u8.ToArray(), 1, null, false)],
            [], PantsPerformanceGoal.Latency);
        var index = SstCodec.ReadHandle(bytes.AsSpan(bytes.Length - DiskFormat.SstFooterSize), 16);
        var indexBytes = SstCodec.ReadBlock(bytes, index);
        var oldDataSize = SstCodec.DecodeIndex(indexBytes)[0].Handle.Size;
        var dataSize = SstCodec.MaximumEncodedDataBlockBytes + 1;
        var delta = checked((long)(dataSize - oldDataSize));
        var firstKeyLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(indexBytes));
        BinaryPrimitives.WriteUInt64LittleEndian(indexBytes.AsSpan(4 + firstKeyLength + 8), dataSize);
        SstBlockCodec.CompressWithTrailer(indexBytes, CompressionAlgorithm.None)
            .CopyTo(bytes.AsSpan(checked((int)index.Offset + 4)));
        var footer = bytes.AsSpan(bytes.Length - DiskFormat.SstFooterSize);
        for (var offset = 0; offset < 64; offset += 16)
        {
            var handle = SstCodec.ReadHandle(footer, offset);
            if (handle.Size != 0)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(footer[offset..], checked(handle.Offset + (ulong)delta));
            }
        }

        BinaryPrimitives.WriteUInt32LittleEndian(footer[80..], DiskFormat.Crc32C(footer[..80]));
        var source = new ExpandedDataBlockSource(bytes, dataSize, delta);
        await using var reader = await AsyncSstReader.OpenAsync(source,
            new FileMeta { Name = "000000_00_00000000000000000001.sst", SizeBytes = (ulong)source.Length },
            CancellationToken.None);

        await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
            reader.ReadDataBlockAsync(0, CancellationToken.None).AsTask());

        Assert.Equal(0, source.DataReadAttempts);
    }

    [Fact]
    public void ShouldRejectV4FooterDespiteValidChecksum()
    {
        var bytes = SstCodec.Encode([], [], PantsPerformanceGoal.Latency);
        var footer = bytes.AsSpan(bytes.Length - DiskFormat.SstFooterSize);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[64..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[80..], DiskFormat.Crc32C(footer[..80]));

        Assert.Throws<PantsCompatibilityException>(() => SstCodec.Decode(bytes));
    }

    sealed class ExpandedDataBlockSource(byte[] metadataBytes, ulong dataSize, long delta) : IAsyncSstSource
    {
        public long Length => metadataBytes.Length + delta;
        public int DataReadAttempts { get; private set; }

        public ValueTask<byte[]> ReadExactlyAsync(long offset, int length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((ulong)offset < dataSize)
            {
                DataReadAttempts++;
                throw new InvalidOperationException("Oversized data body must not be requested.");
            }

            return ValueTask.FromResult(metadataBytes.AsSpan(checked((int)(offset - delta)), length).ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static byte[] RemoveBloomBlock(byte[] original)
    {
        var footer = original.AsSpan(original.Length - DiskFormat.SstFooterSize);
        var bloom = SstCodec.ReadHandle(footer, 48);
        var offset = checked((int)bloom.Offset);
        var size = checked((int)bloom.Size);
        var bytes = new byte[original.Length - size];
        original.AsSpan(0, offset).CopyTo(bytes);
        original.AsSpan(offset + size).CopyTo(bytes.AsSpan(offset));
        var newFooter = bytes.AsSpan(bytes.Length - DiskFormat.SstFooterSize);
        for (var handleOffset = 0; handleOffset < 48; handleOffset += 16)
        {
            var handle = SstCodec.ReadHandle(newFooter, handleOffset);
            if (handle.Offset >= bloom.Offset && handle.Size != 0)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(newFooter[handleOffset..], handle.Offset - bloom.Size);
            }
        }

        newFooter.Slice(48, 16).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(newFooter[80..], DiskFormat.Crc32C(newFooter[..80]));
        return bytes;
    }

    static byte[] Entry(byte flags, ulong sequence, byte[] key, byte[] value)
    {
        using var output = new MemoryStream();
        output.Write([0, checked((byte)key.Length), checked((byte)value.Length)]);
        DiskFormat.WriteUInt64(output, sequence);
        output.WriteByte(flags);
        output.Write(key);
        output.Write(value);
        return output.ToArray();
    }

    static byte[] WithRestarts(byte[] entries, uint[] restarts)
    {
        using var block = new MemoryStream();
        block.Write(entries);
        foreach (var restart in restarts)
        {
            DiskFormat.WriteUInt32(block, restart);
        }

        DiskFormat.WriteUInt32(block, checked((uint)restarts.Length));
        return block.ToArray();
    }
}
