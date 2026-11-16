using System.Buffers.Binary;
using System.IO.Hashing;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsSstBloomSizingTests
{
    const double MidgeDataBlockFalsePositiveRate = 0.001;
    const double LegacyFalsePositiveRate = 0.01;
    const ulong BloomSeedOne = 0x9E37_79B1_85EB_CA87;
    const ulong BloomSeedTwo = 0xC2B2_AE3D_27D4_EB4F;
    const int BloomHeaderSize = 9;

    [Theory]
    [InlineData(1, 64, 8)]
    [InlineData(4, 64, 8)]
    [InlineData(5, 72, 8)]
    [InlineData(100, 1438, 8)]
    [InlineData(1000, 14378, 8)]
    public void ShouldSizeDataBlockBloomLikeMidgeForKeyCount(int keyCount, int expectedBits, int expectedProbes)
    {
        var entries = Entries(keyCount, valueLength: 1);

        var bytes = SstCodec.Encode(entries, [], PantsPerformanceGoal.Latency);

        var bloom = Assert.Single(ReadBlockBlooms(bytes));
        Assert.Equal(keyCount, bloom.KeyCount);
        Assert.Equal(expectedBits, bloom.Bits);
        Assert.Equal(expectedProbes, bloom.Probes);
        Assert.Equal((expectedBits + 7) / 8, bloom.BitBytes);
        Assert.All(entries, entry => Assert.False(SstCodec.GetPointReadDecision(bytes, entry.Key).Rejected));
    }

    [Fact]
    public async Task ShouldPersistMidgeSizedBlockBloomsAndServePointReadsAfterReopen()
    {
        const int keyCount = 1000;
        const int absentProbes = 10_000;
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        var entries = Entries(keyCount, valueLength: 256);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await using (var writer = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                foreach (var entry in entries)
                {
                    writer.Put(entry.Key, entry.Value!);
                }

                await writer.CommitAsync(PantsWriteOptions.Sync);
            }

            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        var sstPath = Assert.Single(Directory.EnumerateFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        var bytes = File.ReadAllBytes(sstPath);
        var blooms = ReadBlockBlooms(bytes);
        Assert.True(blooms.Count > 1, "The workload must span several data blocks.");
        Assert.Equal(keyCount, blooms.Sum(static bloom => bloom.KeyCount));
        Assert.All(blooms, bloom =>
        {
            var expected = ExpectedParameters(bloom.KeyCount, MidgeDataBlockFalsePositiveRate);
            Assert.Equal(expected.Bits, bloom.Bits);
            Assert.Equal(expected.Probes, bloom.Probes);
        });

        // Midge sizes for 0.1% (about 0.11% once the probe count clamps to eight); the legacy
        // 1% sizing lets roughly ten times as many absent keys through to a data-block read.
        var falsePositives = Enumerable.Range(0, absentProbes)
            .Count(index => !SstCodec
                .GetPointReadDecision(bytes, TestBytes.FromString($"key-{index % keyCount:D5}-absent-{index:D5}"))
                .Rejected);
        Assert.InRange(falsePositives, 0, absentProbes * 4 / 1000);

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        foreach (var entry in entries)
        {
            Assert.Equal(entry.Value, (await reader.GetAsync(entry.Key))?.ToArray());
        }

        for (var index = 0; index < 64; index++)
        {
            Assert.Null(await reader.GetAsync(TestBytes.FromString($"key-{index:D5}-absent-{index:D5}")));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReadSstWrittenWithLegacyOnePercentBloomsAfterStoreReopen(bool asynchronous)
    {
        const int keyCount = 1000;
        using var directory = new TemporaryDirectory();
        var entries = Entries(keyCount, valueLength: 256);
        var bytes = WithLegacyBlooms(SstCodec.Encode(entries, [], PantsPerformanceGoal.Latency), entries);
        var legacyBlooms = ReadBlockBlooms(bytes);
        Assert.Equal(keyCount, legacyBlooms.Sum(static bloom => bloom.KeyCount));
        Assert.All(legacyBlooms, bloom =>
        {
            var legacy = ExpectedParameters(bloom.KeyCount, LegacyFalsePositiveRate);
            Assert.Equal(legacy.Bits, bloom.Bits);
            Assert.Equal(legacy.Probes, bloom.Probes);
            Assert.True(bloom.Bits < ExpectedParameters(bloom.KeyCount, MidgeDataBlockFalsePositiveRate).Bits);
        });
        Assert.Equal(keyCount, SstCodec.Decode(bytes).Entries.Count);
        Assert.All(entries, entry =>
        {
            var decision = SstCodec.GetPointReadDecision(bytes, entry.Key);
            Assert.Equal(1, decision.BloomChecks);
            Assert.False(decision.Rejected);
        });
        const string name = "000000_00_00000000000000000001.sst";
        var file = new FileMeta { Name = name, SizeBytes = (ulong)bytes.Length };

        for (var open = 0; open < 2; open++)
        {
            using var store = LocalDiskStore.Open(
                directory.Path,
                new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry()));
            // Opening the store sweeps SSTs its manifest does not own, so the legacy file is put
            // back byte-for-byte before each fresh store reads it.
            File.WriteAllBytes(Path.Combine(directory.Path, "sst", name), bytes);

            foreach (var entry in entries)
            {
                var found = asynchronous
                    ? await store.TryReadPointValueAsync([file], entry.Key, CancellationToken.None)
                    : store.TryReadPointValue([file], entry.Key);
                Assert.NotNull(found);
                Assert.Equal(entry.Value, found.Value);
            }

            var absent = TestBytes.FromString("key-00000-absent");
            Assert.Null(asynchronous
                ? await store.TryReadPointValueAsync([file], absent, CancellationToken.None)
                : store.TryReadPointValue([file], absent));
        }
    }

    static SstEntry[] Entries(int count, int valueLength) => Enumerable.Range(0, count)
        .Select(index =>
        {
            var value = new byte[valueLength];
            new Random(index).NextBytes(value);
            return new SstEntry(
                TestBytes.FromString($"key-{index:D5}"),
                value,
                checked((ulong)index + 1),
                null,
                false);
        })
        .ToArray();

    static (int Bits, int Probes) ExpectedParameters(int keyCount, double falsePositiveRate)
    {
        var bits = Math.Max(
            64,
            checked((int)Math.Ceiling(-keyCount * Math.Log(falsePositiveRate) / Math.Pow(Math.Log(2), 2))));
        var probes = Math.Clamp(
            (int)Math.Round((double)bits / keyCount * Math.Log(2), MidpointRounding.AwayFromZero),
            1,
            8);
        return (bits, probes);
    }

    static List<(int Bits, int KeyCount, int Probes, int BitBytes)> ReadBlockBlooms(byte[] sst)
    {
        var footer = sst.AsSpan(sst.Length - DiskFormat.SstFooterSize);
        ReadOnlySpan<byte> block = SstCodec.ReadBlock(sst, SstCodec.ReadHandle(footer, 48));
        var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(block));
        var data = block[(sizeof(uint) + count * sizeof(uint))..];
        var blooms = new List<(int Bits, int KeyCount, int Probes, int BitBytes)>();
        for (var index = 0; index < count; index++)
        {
            var start = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(block[(sizeof(uint) * (index + 1))..]));
            var end = index + 1 == count
                ? data.Length
                : checked((int)BinaryPrimitives.ReadUInt32LittleEndian(block[(sizeof(uint) * (index + 2))..]));
            var bloom = data[start..end];
            blooms.Add((
                checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bloom)),
                checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bloom[sizeof(uint)..])),
                bloom[8],
                bloom.Length - BloomHeaderSize));
        }

        return blooms;
    }

    /// <summary>
    ///     Rebuilds the block-bloom block the way writers before the 0.1% change sized it, keeping
    ///     every other block byte-identical, so the result is the SST an older writer left on disk.
    /// </summary>
    static byte[] WithLegacyBlooms(byte[] original, IReadOnlyList<SstEntry> orderedEntries)
    {
        var footerOffset = original.Length - DiskFormat.SstFooterSize;
        var bloomHandle = SstCodec.ReadHandle(original.AsSpan(footerOffset), 48);
        using var raw = new MemoryStream();
        var keysPerBlock = ReadBlockBlooms(original).Select(static bloom => bloom.KeyCount).ToArray();
        DiskFormat.WriteUInt32(raw, checked((uint)keysPerBlock.Length));
        var legacyBlooms = new List<byte[]>();
        var firstEntry = 0;
        uint bloomOffset = 0;
        foreach (var blockKeyCount in keysPerBlock)
        {
            var bloom = LegacyBloom(orderedEntries.Skip(firstEntry).Take(blockKeyCount).Select(static entry => entry.Key));
            legacyBlooms.Add(bloom);
            DiskFormat.WriteUInt32(raw, bloomOffset);
            bloomOffset = checked(bloomOffset + (uint)bloom.Length);
            firstEntry += blockKeyCount;
        }

        foreach (var bloom in legacyBlooms)
        {
            raw.Write(bloom);
        }

        var encoded = SstBlockCodec.CompressWithTrailer(raw.ToArray(), PantsPerformanceGoal.Latency);
        var oldOffset = checked((int)bloomHandle.Offset);
        var oldSize = checked((int)bloomHandle.Size);
        var newSize = encoded.Length + sizeof(uint);
        var bytes = new byte[original.Length - oldSize + newSize];
        original.AsSpan(0, oldOffset).CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(oldOffset), checked((uint)encoded.Length));
        encoded.CopyTo(bytes.AsSpan(oldOffset + sizeof(uint)));
        original.AsSpan(oldOffset + oldSize).CopyTo(bytes.AsSpan(oldOffset + newSize));
        var footer = bytes.AsSpan(bytes.Length - DiskFormat.SstFooterSize);
        for (var handleOffset = 0; handleOffset < 48; handleOffset += 16)
        {
            var handle = SstCodec.ReadHandle(footer, handleOffset);
            if (handle.Offset > bloomHandle.Offset && handle.Size != 0)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(
                    footer[handleOffset..],
                    handle.Offset - (ulong)oldSize + (ulong)newSize);
            }
        }

        BinaryPrimitives.WriteUInt64LittleEndian(footer[56..], (ulong)newSize);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[80..], DiskFormat.Crc32C(footer[..80]));
        return bytes;
    }

    static byte[] LegacyBloom(IEnumerable<byte[]> blockKeys)
    {
        var keys = blockKeys.ToArray();
        var (numberOfBits, probes) = ExpectedParameters(keys.Length, LegacyFalsePositiveRate);
        var bloom = new byte[BloomHeaderSize + (numberOfBits + 7) / 8];
        BinaryPrimitives.WriteUInt32LittleEndian(bloom, checked((uint)numberOfBits));
        BinaryPrimitives.WriteUInt32LittleEndian(bloom.AsSpan(sizeof(uint)), checked((uint)keys.Length));
        bloom[8] = checked((byte)probes);
        foreach (var key in keys)
        {
            var firstHash = XxHash3.HashToUInt64(key, unchecked((long)BloomSeedOne));
            var secondHash = XxHash3.HashToUInt64(key, unchecked((long)BloomSeedTwo));
            for (ulong probe = 0; probe < (ulong)probes; probe++)
            {
                var bit = unchecked(firstHash + probe * secondHash) % (ulong)numberOfBits;
                bloom[BloomHeaderSize + checked((int)(bit / 8))] |= checked((byte)(1 << checked((int)(bit % 8))));
            }
        }

        return bloom;
    }
}
