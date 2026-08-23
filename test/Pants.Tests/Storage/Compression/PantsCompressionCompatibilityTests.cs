using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace Cntryl.Pants.Tests.Storage.Compression;

public sealed class PantsCompressionCompatibilityTests
{
    const int SstBlockTrailerSize = 5;
    static readonly string[] Regions = ["apac", "emea", "amer"];
    static readonly string[] Classes = ["standard", "priority"];

    [Fact]
    public void ShouldPreserveExactSstCodecCodes()
    {
        var expected = new[]
        {
            (CompressionAlgorithm.None, (byte)0),
            (CompressionAlgorithm.Lz4, (byte)1),
            (CompressionAlgorithm.Zstd3, (byte)2),
            (CompressionAlgorithm.Zstd9, (byte)3)
        };

        foreach (var (algorithm, code) in expected)
        {
            Assert.Equal(code, (byte)algorithm);
            Assert.Equal(algorithm, SstBlockCodec.ParseAlgorithm(code));
        }

        Assert.Throws<PantsCorruptionException>(() => SstBlockCodec.ParseAlgorithm(4));
        Assert.Throws<PantsCorruptionException>(() => SstBlockCodec.ParseAlgorithm(byte.MaxValue));
    }

    [Fact]
    public void ShouldRejectUnrecognizedCompressionAlgorithmAsCorruption()
    {
        var exception = Assert.Throws<PantsCorruptionException>(
            () => DiskFormat.Decompress("payload"u8.ToArray(), 99));

        Assert.Contains("compression", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldPreserveFiveByteTrailerLayoutWithCrcCoverage()
    {
        var data = "trailer-format-fixture"u8.ToArray();

        var block = SstBlockCodec.CompressWithTrailer(data, CompressionAlgorithm.None);
        var algorithmOffset = block.Length - SstBlockTrailerSize;
        var crcOffset = block.Length - sizeof(uint);
        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(crcOffset));

        Assert.Equal(5, SstBlockCodec.TrailerSize);
        Assert.Equal(data, block.AsSpan(0, algorithmOffset).ToArray());
        Assert.Equal((byte)CompressionAlgorithm.None, block[algorithmOffset]);
        Assert.Equal(DiskFormat.Crc32C(block.AsSpan(0, crcOffset)), storedCrc);
    }

    [Fact]
    public void ShouldPreserveBaselineCompressedBlockFixture()
    {
        var data = StructuredBlock(16 * 1024);
        var cases = new[]
        {
            (CompressionAlgorithm.Lz4, 0xf8ab_776d_208c_bd15UL),
            (CompressionAlgorithm.Zstd3, 0x4e7b_d7fc_d9a0_d5c5UL),
            (CompressionAlgorithm.Zstd9, 0xe2b7_653b_ded1_b28eUL)
        };

        foreach (var (algorithm, expectedDigest) in cases)
        {
            var block = SstBlockCodec.CompressWithTrailer(data, algorithm);

            Assert.Equal((byte)algorithm, block[^SstBlockTrailerSize]);
            Assert.Equal(expectedDigest, XxHash3.HashToUInt64(block));
        }
    }

    [Fact]
    public void ShouldRoundTripEveryEmittedSstBlockDeterministically()
    {
        var data = StructuredBlock(16 * 1024);
        var fixedAlgorithms = new[]
        {
            CompressionAlgorithm.None,
            CompressionAlgorithm.Lz4,
            CompressionAlgorithm.Zstd3,
            CompressionAlgorithm.Zstd9
        };

        foreach (var algorithm in fixedAlgorithms)
        {
            var first = SstBlockCodec.CompressWithTrailer(data, algorithm);
            var second = SstBlockCodec.CompressWithTrailer(data, algorithm);

            Assert.Equal(first, second);
            Assert.Equal(data, SstBlockCodec.DecompressWithTrailer(first));
        }

        var firstAdaptive = SstBlockCodec.CompressWithTrailer(data, PantsPerformanceGoal.Throughput);
        var secondAdaptive = SstBlockCodec.CompressWithTrailer(data, PantsPerformanceGoal.Throughput);

        Assert.Equal(firstAdaptive, secondAdaptive);
        Assert.Equal(data, SstBlockCodec.DecompressWithTrailer(firstAdaptive));
    }

    [Fact]
    public void ShouldRejectInvalidSstBlockTrailers()
    {
        var data = StructuredBlock(1024);
        var valid = SstBlockCodec.CompressWithTrailer(data, CompressionAlgorithm.None);
        var corrupt = valid.ToArray();
        corrupt[^1] ^= 0x01;
        var unknown = data.Concat(new[] { byte.MaxValue }).ToArray();
        Array.Resize(ref unknown, unknown.Length + sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(
            unknown.AsSpan(unknown.Length - sizeof(uint)),
            DiskFormat.Crc32C(unknown.AsSpan(0, unknown.Length - sizeof(uint))));

        var corruptError =
            Assert.Throws<PantsCorruptionException>(() => SstBlockCodec.DecompressWithTrailer(corrupt));
        var truncatedError =
            Assert.Throws<PantsCorruptionException>(() => SstBlockCodec.DecompressWithTrailer(valid.AsSpan(0, 4)));
        var unknownError =
            Assert.Throws<PantsCorruptionException>(() => SstBlockCodec.DecompressWithTrailer(unknown));

        Assert.Contains("CRC32C mismatch", corruptError.Message, StringComparison.Ordinal);
        Assert.Contains("too small for trailer", truncatedError.Message, StringComparison.Ordinal);
        Assert.Contains("unknown compression algorithm code", unknownError.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((byte)4)]
    [InlineData((byte)5)]
    [InlineData(byte.MaxValue)]
    public void ShouldRejectNonshippingCodecCodesWithoutFallback(byte code)
    {
        var block = WithTrailer("payload"u8, code);

        var error = Assert.Throws<PantsCorruptionException>(() => SstBlockCodec.DecompressWithTrailer(block));

        Assert.Equal(PantsErrorCode.Corruption, error.Code);
        Assert.Contains("unknown compression algorithm code", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((byte)CompressionAlgorithm.Lz4)]
    [InlineData((byte)CompressionAlgorithm.Zstd9)]
    public void ShouldRejectCorruptCompressedPayloadForEveryShippingCodec(byte algorithmCode)
    {
        var algorithm = (CompressionAlgorithm)algorithmCode;
        var block = SstBlockCodec
            .CompressWithTrailer(StructuredBlock(16 * 1024), algorithm)
            .ToArray();
        if (algorithm == CompressionAlgorithm.Lz4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(block, 64 * 1024 * 1024U + 1);
        }
        else
        {
            block[0] ^= 0xff;
        }

        RewriteTrailerCrc(block);

        var error = Assert.Throws<PantsCorruptionException>(() => SstBlockCodec.DecompressWithTrailer(block));

        Assert.Equal(PantsErrorCode.Corruption, error.Code);
    }

    [Fact]
    public async Task ShouldRoundTripEdgeCaseValuesWhenWrittenThroughFullSstPipeline()
    {
        using var directory = new TemporaryDirectory();
        var incompressible = SeededBytes(16 * 1024, 0x8f21_49da);
        await using (var database =
                     await PantsDatabase.OpenAsync(LocalOptions(directory.Path, PantsPerformanceGoal.Latency)))
        {
            await WriteRecordsAndFlushAsync(
                database,
                database.DefaultColumnFamily,
                [
                    new KeyValuePair<byte[], byte[]>("empty"u8.ToArray(), []),
                    new KeyValuePair<byte[], byte[]>("random"u8.ToArray(), incompressible)
                ]);
        }

        await using var reopened =
            await PantsDatabase.OpenAsync(LocalOptions(directory.Path, PantsPerformanceGoal.Latency));
        await using var read = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Empty((await read.GetAsync("empty"u8.ToArray()))!.Value.ToArray());
        Assert.Equal(incompressible, (await read.GetAsync("random"u8.ToArray()))!.Value.ToArray());
    }

    [Fact]
    public async Task ShouldPreserveDataGivenCompressionPolicyChangeWhenReopeningPopulatedDatabase()
    {
        using var directory = new TemporaryDirectory();
        var latencyRecords = AdaptiveRecords("latency", 48);
        var economyRecords = AdaptiveRecords("economy", 48);
        await using (var latency =
                     await PantsDatabase.OpenAsync(LocalOptions(directory.Path, PantsPerformanceGoal.Latency)))
        {
            var family = await latency.CreateColumnFamilyAsync("policies");
            await WriteRecordsAndFlushAsync(latency, family, latencyRecords);
        }

        await using var economy =
            await PantsDatabase.OpenAsync(LocalOptions(directory.Path, PantsPerformanceGoal.Economy));
        var reopenedFamily = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await economy.GetColumnFamilyAsync("policies"));
        await WriteRecordsAndFlushAsync(economy, reopenedFamily, economyRecords);
        await using var read = await economy.BeginTransactionAsync(reopenedFamily, PantsTransactionMode.ReadOnly);
        var rows = await ReadAllAsync(read);

        Assert.Equal(latencyRecords.Length + economyRecords.Length, rows.Count);
        foreach (var record in latencyRecords.Concat(economyRecords))
        {
            Assert.Equal(record.Value, (await read.GetAsync(record.Key))!.Value.ToArray());
        }
    }

    [Fact]
    public async Task ShouldSelectCurrentPolicyForNewBlocksWhenCompactingAfterGoalChange()
    {
        using var directory = new TemporaryDirectory();
        var batches = Enumerable.Range(0, 4)
            .Select(batch => AdaptiveRecords($"policy-{batch}", 48))
            .ToArray();
        await using (var latency =
                     await PantsDatabase.OpenAsync(LocalOptions(directory.Path, PantsPerformanceGoal.Latency)))
        {
            var family = await latency.CreateColumnFamilyAsync("policies");
            foreach (var batch in batches[..3])
            {
                await WriteRecordsAndFlushAsync(latency, family, batch);
            }
        }

        Assert.Contains(
            SortedSstFiles(directory.Path).SelectMany(static file => SstBlockAlgorithms(file.Bytes)),
            algorithm => algorithm == (byte)CompressionAlgorithm.Lz4);

        await using (var economy =
                     await PantsDatabase.OpenAsync(LocalOptions(directory.Path, PantsPerformanceGoal.Economy)))
        {
            var family = Assert.IsAssignableFrom<IPantsColumnFamily>(
                await economy.GetColumnFamilyAsync("policies"));
            await WriteRecordsAndFlushAsync(economy, family, batches[3]);
            Assert.Contains(
                SortedSstFiles(directory.Path).SelectMany(static file => SstBlockAlgorithms(file.Bytes)),
                algorithm => algorithm == (byte)CompressionAlgorithm.Zstd9);
            await economy.CompactAllAsync();
        }

        var report = await PantsDatabase.VerifyPathAsync(directory.Path);
        Assert.True(report.Authoritative);
        Assert.Contains(
            SortedSstFiles(directory.Path).SelectMany(static file => SstBlockAlgorithms(file.Bytes)),
            algorithm => algorithm == (byte)CompressionAlgorithm.Zstd9);

        await using var reopened =
            await PantsDatabase.OpenAsync(LocalOptions(directory.Path, PantsPerformanceGoal.Throughput));
        var reopenedFamily = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("policies"));
        await using var read = await reopened.BeginTransactionAsync(reopenedFamily, PantsTransactionMode.ReadOnly);
        var rows = await ReadAllAsync(read);
        Assert.Equal(batches.Sum(static batch => batch.Length), rows.Count);
        foreach (var record in batches.SelectMany(static batch => batch))
        {
            Assert.Equal(record.Value, (await read.GetAsync(record.Key))!.Value.ToArray());
        }
    }

    [Fact]
    public async Task ShouldReportMeaningfulErrorGivenFooterCorruptionWhenRunningExplicitVerification()
    {
        using var directory = new TemporaryDirectory();
        await WriteFreshAdaptiveDatabaseAsync(directory.Path, AdaptiveRecords("footer", 32));
        var sstPath = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        var bytes = await File.ReadAllBytesAsync(sstPath);
        bytes[bytes.Length - DiskFormat.SstFooterSize + 8] ^= 0x01;
        await File.WriteAllBytesAsync(sstPath, bytes);

        var error = await Assert.ThrowsAsync<PantsCorruptionException>(() =>
            PantsDatabase.VerifyPathAsync(directory.Path).AsTask());

        Assert.Equal(PantsErrorCode.Corruption, error.Code);
        Assert.Contains(
            "checksum",
            FlattenExceptionMessages(error),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShouldPreserveCandidateAdaptiveSstAcrossStrictReopen()
    {
        using var directory = new TemporaryDirectory();
        var records = AdaptiveRecords("adaptive:key", 384);
        var expectedDigest = CanonicalDataDigest(records);
        await WriteFreshAdaptiveDatabaseAsync(directory.Path, records);

        var report = await PantsDatabase.VerifyPathAsync(directory.Path);
        await using var reopened =
            await PantsDatabase.OpenAsync(LocalOptions(directory.Path, PantsPerformanceGoal.Throughput));
        var family = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("adaptive"));
        await using var read = await reopened.BeginTransactionAsync(family, PantsTransactionMode.ReadOnly);
        var rows = await ReadAllAsync(read);

        Assert.Equal(PantsEngineHealth.Healthy, report.Health);
        Assert.True(report.Authoritative);
        Assert.True(report.SstFilesVerified >= 1);
        Assert.Equal(records[0].Value, (await read.GetAsync("adaptive:key:0000"u8.ToArray()))!.Value.ToArray());
        Assert.Equal(records[192].Value, (await read.GetAsync("adaptive:key:0192"u8.ToArray()))!.Value.ToArray());
        Assert.Equal(records[383].Value, (await read.GetAsync("adaptive:key:0383"u8.ToArray()))!.Value.ToArray());
        Assert.Equal(records.Length, rows.Count);
        Assert.Equal(expectedDigest, CanonicalDataDigest(rows));
    }

    [Fact]
    public async Task ShouldStrictlyReopenCompletedAdaptiveCompaction()
    {
        using var directory = new TemporaryDirectory();
        var firstBatch = AdaptiveRecords("compacted:a", 192);
        var secondBatch = AdaptiveRecords("compacted:b", 192);
        var expectedRecords = firstBatch.Concat(secondBatch)
            .OrderBy(static record => record.Key, ByteArrayComparer.Instance)
            .ToArray();
        await using (var database =
                     await PantsDatabase.OpenAsync(LocalOptions(directory.Path, PantsPerformanceGoal.Throughput)))
        {
            var family = await database.CreateColumnFamilyAsync("adaptive");
            await WriteRecordsAndFlushAsync(database, family, firstBatch);
            await WriteRecordsAndFlushAsync(database, family, secondBatch);
            await database.CompactAllAsync();
        }

        var report = await PantsDatabase.VerifyPathAsync(directory.Path);
        await using var reopened =
            await PantsDatabase.OpenAsync(LocalOptions(directory.Path, PantsPerformanceGoal.Throughput));
        var reopenedFamily = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("adaptive"));
        await using var read = await reopened.BeginTransactionAsync(reopenedFamily, PantsTransactionMode.ReadOnly);
        var rows = await ReadAllAsync(read);

        Assert.Equal(PantsEngineHealth.Healthy, report.Health);
        Assert.True(report.Authoritative);
        Assert.True(report.SstFilesVerified >= 1);
        Assert.Equal(firstBatch[0].Value, (await read.GetAsync("compacted:a:0000"u8.ToArray()))!.Value.ToArray());
        Assert.Equal(firstBatch[191].Value, (await read.GetAsync("compacted:a:0191"u8.ToArray()))!.Value.ToArray());
        Assert.Equal(secondBatch[191].Value, (await read.GetAsync("compacted:b:0191"u8.ToArray()))!.Value.ToArray());
        Assert.Equal(expectedRecords.Length, rows.Count);
        Assert.Equal(CanonicalDataDigest(expectedRecords), CanonicalDataDigest(rows));
    }

    [Fact]
    public async Task ShouldProduceByteIdenticalSstFilesGivenIdenticalAdaptiveInput()
    {
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();
        var records = AdaptiveRecords("adaptive:key", 384);

        await WriteFreshAdaptiveDatabaseAsync(first.Path, records);
        await WriteFreshAdaptiveDatabaseAsync(second.Path, records);
        var firstSsts = SortedSstFiles(first.Path);
        var secondSsts = SortedSstFiles(second.Path);

        Assert.NotEmpty(firstSsts);
        Assert.Equal(firstSsts.Select(static file => file.Name), secondSsts.Select(static file => file.Name));
        Assert.Equal(firstSsts.Length, secondSsts.Length);
        for (var index = 0; index < firstSsts.Length; index++)
        {
            Assert.Equal(firstSsts[index].Bytes, secondSsts[index].Bytes);
        }
    }

    static PantsOpenOptions LocalOptions(string path, PantsPerformanceGoal performanceGoal) =>
        PantsOpenOptions.Local(path)
            .WithPerformanceGoal(performanceGoal)
            .WithWorkloadProfile(PantsWorkloadProfile.WriteHeavy)
            .WithRecoveryPolicy(PantsRecoveryPolicy.Strict)
            .WithBackgroundCompaction(false);

    static async Task WriteRecordsAndFlushAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        IReadOnlyList<KeyValuePair<byte[], byte[]>> records)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        foreach (var record in records)
        {
            transaction.Put(record.Key, record.Value);
        }

        await transaction.CommitAsync(PantsWriteOptions.Sync);
        await database.FlushAsync(family);
    }

    static async Task WriteFreshAdaptiveDatabaseAsync(
        string path,
        IReadOnlyList<KeyValuePair<byte[], byte[]>> records)
    {
        await using var database = await PantsDatabase.OpenAsync(
            LocalOptions(path, PantsPerformanceGoal.Throughput));
        var family = await database.CreateColumnFamilyAsync("adaptive");
        await WriteRecordsAndFlushAsync(database, family, records);
    }

    static byte[] StructuredBlock(int size)
    {
        var pattern = "account=0042|region=east|status=active|segment=business|"u8.ToArray();
        var bytes = new byte[size];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = pattern[index % pattern.Length];
        }

        return bytes;
    }

    static KeyValuePair<byte[], byte[]>[] AdaptiveRecords(string prefix, int count) =>
        Enumerable.Range(0, count)
            .Select(index =>
            {
                var pattern = Encoding.UTF8.GetBytes(
                    $"order={index:0000}|region={Regions[index % Regions.Length]}|" +
                    $"state=committed|class={Classes[index % Classes.Length]}|");
                var value = new byte[4 * 1024];
                for (var valueIndex = 0; valueIndex < value.Length; valueIndex++)
                {
                    value[valueIndex] = pattern[valueIndex % pattern.Length];
                }

                return new KeyValuePair<byte[], byte[]>(
                    Encoding.UTF8.GetBytes($"{prefix}:{index:0000}"),
                    value);
            })
            .ToArray();

    static byte[] SeededBytes(int size, uint seed)
    {
        var state = seed;
        return Enumerable.Range(0, size)
            .Select(_ =>
            {
                state = unchecked(state * 1_664_525 + 1_013_904_223);
                return (byte)(state >> 24);
            })
            .ToArray();
    }

    static byte[] WithTrailer(ReadOnlySpan<byte> payload, byte algorithm)
    {
        var block = new byte[payload.Length + SstBlockTrailerSize];
        payload.CopyTo(block);
        block[payload.Length] = algorithm;
        BinaryPrimitives.WriteUInt32LittleEndian(
            block.AsSpan(payload.Length + 1),
            DiskFormat.Crc32C(block.AsSpan(0, payload.Length + 1)));
        return block;
    }

    static void RewriteTrailerCrc(byte[] block)
    {
        var crcOffset = block.Length - sizeof(uint);
        BinaryPrimitives.WriteUInt32LittleEndian(
            block.AsSpan(crcOffset),
            DiskFormat.Crc32C(block.AsSpan(0, crcOffset)));
    }

    static (string Name, byte[] Bytes)[] SortedSstFiles(string path) =>
        Directory.GetFiles(Path.Combine(path, "sst"), "*.sst")
            .OrderBy(static file => Path.GetFileName(file), StringComparer.Ordinal)
            .Select(static file => (Path.GetFileName(file), File.ReadAllBytes(file)))
            .ToArray();

    static List<byte> SstBlockAlgorithms(byte[] bytes)
    {
        var footerStart = bytes.Length - DiskFormat.SstFooterSize;
        var algorithms = new List<byte>();
        var cursor = 0;
        while (cursor < footerStart)
        {
            var payloadLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor)));
            var blockEnd = checked(cursor + sizeof(uint) + payloadLength);
            Assert.InRange(blockEnd, 0, footerStart);
            algorithms.Add(bytes[blockEnd - SstBlockTrailerSize]);
            cursor = blockEnd;
        }

        Assert.Equal(footerStart, cursor);
        return algorithms;
    }

    static async Task<IReadOnlyList<KeyValuePair<byte[], byte[]>>> ReadAllAsync(
        IPantsTransaction transaction)
    {
        var rows = new List<KeyValuePair<byte[], byte[]>>();
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        await foreach (var entry in scan)
        {
            rows.Add(new KeyValuePair<byte[], byte[]>(entry.Key.ToArray(), entry.Value.ToArray()));
        }

        return rows;
    }

    static ulong CanonicalDataDigest(IEnumerable<KeyValuePair<byte[], byte[]>> records)
    {
        using var canonical = new MemoryStream();
        Span<byte> length = stackalloc byte[sizeof(uint)];
        foreach (var record in records)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)record.Key.Length));
            canonical.Write(length);
            canonical.Write(record.Key);
            BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)record.Value.Length));
            canonical.Write(length);
            canonical.Write(record.Value);
        }

        return XxHash3.HashToUInt64(canonical.ToArray());
    }

    static string FlattenExceptionMessages(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }
}
