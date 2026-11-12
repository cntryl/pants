using System.Text;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeWireGoldenTests
{
    const string PinnedMidgeSha = "7d39f86217fdb07191a83bd885a514dd5ea9723f";

    [Fact]
    public void ShouldDecodeFormatMarkerGivenPinnedMidgeWireGolden()
    {
        var bytes = ReadFixture("FORMAT");

        Assert.Equal("midge-format-version=3\n", Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData("wal-tlv-put-v1.bin")]
    [InlineData("wal-tlv-insert-v1.bin")]
    [InlineData("wal-tlv-delete-v1.bin")]
    [InlineData("wal-tlv-delete-range-v1.bin")]
    [InlineData("wal-tlv-empty-value-v1.bin")]
    [InlineData("wal-txn-batch-v1.bin")]
    public void ShouldRejectPinnedLegacyWalPayloadAsIncompatible(string fixtureName)
    {
        var bytes = ReadFixture(fixtureName);

        Assert.Throws<PantsCompatibilityException>(() => WalCodec.DecodeRecord(bytes));
    }

    [Fact]
    public void ShouldRejectPinnedLegacyWalFrameAsIncompatible()
    {
        var bytes = ReadFixture("wal-frame-put-v1.bin");

        Assert.Throws<PantsCompatibilityException>(() => WalFrameReader.Visit(bytes, static (_, _) => { }));
    }

    [Theory]
    [InlineData(2, 6UL, "one")]
    [InlineData(3, 9UL, "two")]
    [InlineData(4, 12UL, "three")]
    [InlineData(5, 15UL, null)]
    public void ShouldDecodeUnmodifiedMidgeV2WalSegment(
        int segmentId, ulong expectedSequence, string? expectedValue)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compatibility", "Midge",
            "c684310d9d8ce8ecd1151c47151d4bea98b348cf", "wal", $"{segmentId:D20}.wal");
        var bytes = File.ReadAllBytes(path);
        var records = new List<WalRecord>();

        WalFrameReader.Visit(bytes, (record, _) => records.Add(record));

        var batch = Assert.Single(records);
        Assert.Equal(WalOperation.TransactionBatch, batch.Operation);
        Assert.Equal(expectedSequence, batch.Sequence);
        Assert.Equal(1UL, batch.WriterEpoch);
        var mutations = WalCodec.DecodeTransactionBatch(batch, out var commitSequence, out var writerEpoch);
        var mutation = Assert.Single(mutations);
        Assert.Equal(expectedSequence, commitSequence);
        Assert.Equal(1UL, writerEpoch);
        Assert.Equal(2U, mutation.ColumnFamilyId);
        Assert.Equal("deleted"u8.ToArray(), mutation.Key);
        Assert.Equal(expectedValue is null ? WalOperation.Delete : WalOperation.Put, mutation.Operation);
        Assert.Equal(expectedValue is null ? null : Encoding.UTF8.GetBytes(expectedValue), mutation.Value);
        Assert.Equal(expectedSequence - 1, mutation.Sequence);
    }

    [Theory]
    [InlineData("sst-block-none-v1.bin", (byte)0)]
    [InlineData("sst-block-lz4-v1.bin", (byte)1)]
    [InlineData("sst-block-zstd3-v1.bin", (byte)2)]
    [InlineData("sst-block-zstd9-v1.bin", (byte)3)]
    public void ShouldDecodeBlockGivenPinnedMidgeSstCodecGolden(
        string fixtureName,
        byte expectedAlgorithmCode)
    {
        var input = ReadFixture("sst-block-input-v1.bin");
        var block = ReadFixture(fixtureName);

        var decoded = SstBlockCodec.DecompressWithTrailer(block);

        Assert.Equal(16 * 1024, input.Length);
        Assert.Equal(expectedAlgorithmCode, block[^SstBlockCodec.TrailerSize]);
        Assert.Equal(input, decoded);
    }

    [Fact]
    public void ShouldMatchPantsLayoutGivenPinnedMidgeCloudObjectKeyGolden()
    {
        var entries = Encoding.UTF8.GetString(ReadFixture("cloud-object-keys-v1.txt"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Split('=', 2))
            .ToDictionary(static parts => parts[0], static parts => parts[1], StringComparer.Ordinal);

        Assert.Equal(PantsCloudObjectLayout.WalPrefix, entries["wal_prefix"]);
        Assert.Equal(PantsCloudObjectLayout.WalCatalogObjectKey, entries["wal_catalog_object_key"]);
        Assert.Equal("00000000000000000011.wal", entries["wal_segment_file_name"]);
        Assert.Equal(
            PantsCloudObjectLayout.WalSegmentObjectKey(7, 11),
            entries["wal_segment_object_key"]);
        Assert.Equal(PantsCloudObjectLayout.SstPrefix, entries["sst_prefix"]);
        Assert.Equal("000007_02_00000000000000000042.sst", entries["sst_file_name"]);
        Assert.Equal(
            "sst/000007_02_00000000000000000042.sst",
            entries["sst_object_key"]);
        Assert.Equal(
            "sst/000007_02_00000000000000000042.sst.tmp",
            entries["sst_temp_object_key"]);
        Assert.Equal(PantsCloudObjectLayout.MetadataPrefix, entries["metadata_prefix"]);
        Assert.Equal("metadata/FORMAT", entries["metadata_format_object_key"]);
        Assert.Equal(
            "metadata/manifest.snapshot.json",
            entries["metadata_manifest_snapshot_object_key"]);
        Assert.Equal("metadata/manifest.json", entries["metadata_manifest_object_key"]);
        Assert.Equal(
            "metadata/manifest.journal",
            entries["metadata_manifest_journal_object_key"]);
        Assert.Equal("metadata/intent_log.json", entries["metadata_intent_log_object_key"]);
        Assert.Equal(
            PantsCloudObjectLayout.DdlRegistryObjectKey,
            entries["metadata_ddl_registry_object_key"]);
        Assert.Equal(PantsCloudObjectLayout.LeaseObjectKey, entries["lease_object_key"]);
    }

    static byte[] ReadFixture(string fileName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Compatibility",
            "Wire",
            PinnedMidgeSha,
            fileName);
        return File.ReadAllBytes(path);
    }
}
