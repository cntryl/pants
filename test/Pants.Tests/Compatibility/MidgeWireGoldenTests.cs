using System.Text;

namespace Cntryl.Pants.Tests.Compatibility;

public sealed class MidgeWireGoldenTests
{
    const string PinnedMidgeSha = "c5ffc2d3284c76b6f7cd03444a5b0a38ae8bbc33";

    [Fact]
    public void ShouldDecodeFormatMarkerGivenPinnedMidgeWireGolden()
    {
        var bytes = ReadFixture("FORMAT");

        Assert.Equal("midge-format-version=3\n", Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData("wal-tlv-put-v1.bin", (byte)0)]
    [InlineData("wal-tlv-insert-v1.bin", (byte)1)]
    [InlineData("wal-tlv-delete-v1.bin", (byte)2)]
    [InlineData("wal-tlv-delete-range-v1.bin", (byte)3)]
    public void ShouldDecodeMutationGivenPinnedMidgeWalTlvGolden(
        string fixtureName,
        byte expectedOperationCode)
    {
        var expectedOperation = (MidgeWalOperation)expectedOperationCode;
        var bytes = ReadFixture(fixtureName);
        var record = MidgeWalCodec.DecodeRecord(bytes);

        Assert.Equal(bytes, MidgeWalCodec.EncodeRecord(record));
        Assert.Equal(expectedOperation, record.Operation);
        Assert.NotEmpty(record.Key);
        Assert.True(record.Sequence > 0);
        Assert.True(record.WriterEpoch > 0);
        if (expectedOperation is MidgeWalOperation.Put or MidgeWalOperation.Insert)
        {
            _ = Assert.IsType<byte[]>(record.Value);
        }
        else
        {
            Assert.Null(record.Value);
        }

        if (expectedOperation == MidgeWalOperation.DeleteRange)
        {
            Assert.NotEmpty(Assert.IsType<byte[]>(record.RangeEnd));
        }
        else
        {
            Assert.Null(record.RangeEnd);
        }
    }

    [Fact]
    public void ShouldPreserveTtlGivenPinnedMidgePutTlvGolden()
    {
        var record = MidgeWalCodec.DecodeRecord(ReadFixture("wal-tlv-put-v1.bin"));

        Assert.True(record.Expiration is > 0);
    }

    [Fact]
    public void ShouldPreservePresentEmptyValueGivenPinnedMidgeWalTlvGolden()
    {
        var bytes = ReadFixture("wal-tlv-empty-value-v1.bin");
        var record = MidgeWalCodec.DecodeRecord(bytes);

        Assert.Equal(bytes, MidgeWalCodec.EncodeRecord(record));
        Assert.Equal(MidgeWalOperation.Put, record.Operation);
        Assert.Empty(Assert.IsType<byte[]>(record.Value));
    }

    [Fact]
    public void ShouldDecodeFrameGivenPinnedMidgeWalFrameGolden()
    {
        var bytes = ReadFixture("wal-frame-put-v1.bin");
        var records = new List<MidgeWalRecord>();

        MidgeWalFrameReader.Visit(
            bytes,
            (record, _) => records.Add(record));

        var record = Assert.Single(records);
        var expected = MidgeWalCodec.DecodeRecord(ReadFixture("wal-tlv-put-v1.bin"));
        Assert.Equal(expected.ColumnFamilyId, record.ColumnFamilyId);
        Assert.Equal(expected.Operation, record.Operation);
        Assert.Equal(expected.Key, record.Key);
        Assert.Equal(expected.Value, record.Value);
        Assert.Equal(expected.Sequence, record.Sequence);
        Assert.Equal(expected.Expiration, record.Expiration);
        Assert.Equal(expected.RangeEnd, record.RangeEnd);
        Assert.Equal(expected.TransactionId, record.TransactionId);
        Assert.Equal(expected.WriterEpoch, record.WriterEpoch);

        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "wal-frame-put-v1.bin");
        using (var handle = File.OpenHandle(
                   path,
                   FileMode.CreateNew,
                   FileAccess.Write))
        {
            MidgeWalCodec.AppendFrame(handle, 0, MidgeWalCodec.EncodeRecord(record));
        }

        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void ShouldDecodeAtomicBatchGivenPinnedMidgeWalGolden()
    {
        var bytes = ReadFixture("wal-txn-batch-v1.bin");
        var record = MidgeWalCodec.DecodeRecord(bytes);

        var mutations = MidgeWalCodec.DecodeTransactionBatch(
            record,
            out var commitSequence,
            out var writerEpoch);

        Assert.Equal(bytes, MidgeWalCodec.EncodeRecord(record));
        Assert.Equal(MidgeWalOperation.TransactionBatch, record.Operation);
        Assert.Equal(record.Sequence, commitSequence);
        Assert.Equal(record.WriterEpoch, writerEpoch);
        Assert.Collection(
            mutations,
            mutation =>
            {
                Assert.Equal(MidgeWalOperation.Put, mutation.Operation);
                _ = Assert.IsType<byte[]>(mutation.Value);
                Assert.True(mutation.Expiration is > 0);
            },
            mutation =>
            {
                Assert.Equal(MidgeWalOperation.DeleteRange, mutation.Operation);
                Assert.NotEmpty(Assert.IsType<byte[]>(mutation.RangeEnd));
            });
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

        var decoded = MidgeSstBlockCodec.DecompressWithTrailer(block);

        Assert.Equal(16 * 1024, input.Length);
        Assert.Equal(expectedAlgorithmCode, block[^MidgeSstBlockCodec.TrailerSize]);
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
