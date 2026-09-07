using System.Buffers.Binary;

namespace Cntryl.Pants.Fuzz;

/// <summary>
///     Fuzz targets for the persisted-format decoders, seeded from the committed Midge fixtures so
///     exploration starts from bytes a real engine produced rather than from noise.
/// </summary>
public sealed class DecoderFuzzTests
{
    [Fact]
    public void ShouldFailClosedDecodingCorruptWalRecords()
    {
        DecoderFuzzHarness.Explore(
            "WalCodec.DecodeRecord",
            LoadSeeds("wal-tlv-*.bin", "wal-frame-*.bin"),
            static bytes => WalCodec.DecodeRecord(bytes));
    }

    [Fact]
    public void ShouldFailClosedDecodingCorruptTransactionBatches()
    {
        DecoderFuzzHarness.Explore(
            "WalCodec.DecodeTransactionBatch",
            LoadSeeds("wal-txn-batch-*.bin"),
            static bytes => WalCodec.DecodeTransactionBatch(bytes, out _, out _));
    }

    [Fact]
    public void ShouldFailClosedDecodingCorruptSstDataBlocks()
    {
        DecoderFuzzHarness.Explore(
            "SstCodec.DecodeDataBlock",
            LoadSeeds("sst-block-*.bin"),
            static bytes => SstCodec.DecodeDataBlock(bytes));
    }

    [Fact]
    public void ShouldFailClosedDecodingCorruptSstFiles()
    {
        DecoderFuzzHarness.Explore(
            "SstCodec.Decode",
            LoadSeeds("*.sst"),
            static bytes => SstCodec.Decode(bytes));
    }

    /// <summary>
    ///     A declared count that would imply a huge allocation must be rejected on the strength of
    ///     the declaration, before the allocation is attempted.
    /// </summary>
    [Fact]
    public void ShouldRejectDeclaredCountBombsBeforeAllocating()
    {
        var seeds = LoadSeeds("wal-txn-batch-*.bin");
        var bomb = seeds[0].ToArray();

        // The operation count sits after the batch magic, version, transaction id, begin and commit
        // sequences.
        BinaryPrimitives.WriteUInt32LittleEndian(
            bomb.AsSpan(3 + (3 * sizeof(ulong))),
            int.MaxValue);

        var error = Assert.ThrowsAny<PantsException>(() =>
            WalCodec.DecodeTransactionBatch(bomb, out _, out _));
        Assert.IsAssignableFrom<PantsIOException>(error);
    }

    static List<byte[]> LoadSeeds(params string[] patterns)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compatibility");
        var seeds = new List<byte[]>();
        foreach (var pattern in patterns)
        {
            foreach (var path in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length is > 0 and <= 1024 * 1024)
                {
                    seeds.Add(bytes);
                }
            }
        }

        Assert.NotEmpty(seeds);
        return seeds;
    }
}
