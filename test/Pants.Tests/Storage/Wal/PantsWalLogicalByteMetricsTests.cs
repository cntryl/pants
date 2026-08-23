namespace Pants.Tests;

public sealed class PantsWalLogicalByteMetricsTests
{
    [Fact]
    public async Task ShouldCountPutKeyAndValueBytesExactlyWhenVerifyingAndRecovering()
    {
        var record = CreateRecord(
            MidgeWalOperation.Put,
            "alpha",
            "bravo",
            sequence: 1);

        await AssertWalMetricsAsync([record], expectedRecords: 1, expectedBytes: 10);
    }

    [Fact]
    public async Task ShouldCountDeleteRangeBoundaryBytesExactlyWhenVerifyingAndRecovering()
    {
        var record = CreateRecord(
            MidgeWalOperation.DeleteRange,
            "alpha",
            value: null,
            sequence: 1,
            rangeEnd: "omega");

        await AssertWalMetricsAsync([record], expectedRecords: 1, expectedBytes: 10);
    }

    [Fact]
    public async Task ShouldCountOuterBatchKeyAndPayloadBytesExactlyWhenVerifyingAndRecovering()
    {
        var record = MidgeWalCodec.EncodeTransactionBatch(
            transactionId: 7,
            beginSequence: 1,
            writerEpoch: 1,
            [
                new MidgeWalMutation(
                    ColumnFamilyId: 0,
                    MidgeWalOperation.Put,
                    "batch-key"u8.ToArray(),
                    "batch-value"u8.ToArray(),
                    Sequence: 0,
                    Expiration: null,
                    RangeEnd: null)
            ]);

        await AssertWalMetricsAsync([record], expectedRecords: 1, expectedBytes: 78);
    }

    [Fact]
    public async Task ShouldCountEncodedMarkerKeysExactlyWhenVerifyingAndRecovering()
    {
        var begin = MidgeWalCodec.EncodeTransactionMarker(
            MidgeWalOperation.TransactionBegin,
            transactionId: 7,
            sequence: 1,
            writerEpoch: 1);
        var commit = MidgeWalCodec.EncodeTransactionMarker(
            MidgeWalOperation.TransactionCommit,
            transactionId: 7,
            sequence: 2,
            writerEpoch: 1);

        await AssertWalMetricsAsync([begin, commit], expectedRecords: 2, expectedBytes: 6);
    }

    static async Task AssertWalMetricsAsync(
        IReadOnlyList<byte[]> records,
        long expectedRecords,
        long expectedBytes)
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
        }

        await File.WriteAllBytesAsync(
            Path.Combine(directory.Path, "wal", "wal.log"),
            Frame(records));

        var verification = await PantsDatabase.VerifyPathAsync(directory.Path);
        Assert.Equal(expectedRecords, verification.WalRecoveryRecordsReplayed);
        Assert.Equal(expectedBytes, verification.WalRecoveryBytesReplayed);

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var recovery = await reopened.GetRecoveryMetricsAsync();
        Assert.Equal(expectedRecords, recovery.WalRecordsReplayed);
        Assert.Equal(expectedBytes, recovery.WalBytesReplayed);
    }

    static byte[] CreateRecord(
        MidgeWalOperation operation,
        string key,
        string? value,
        ulong sequence,
        string? rangeEnd = null) =>
        MidgeWalCodec.EncodeRecord(new MidgeWalRecord(
            ColumnFamilyId: 0,
            operation,
            TestBytes.FromString(key),
            value is null ? null : TestBytes.FromString(value),
            sequence,
            Expiration: null,
            rangeEnd is null ? null : TestBytes.FromString(rangeEnd),
            TransactionId: null,
            WriterEpoch: 1));

    static byte[] Frame(IEnumerable<byte[]> payloads)
    {
        using var stream = new MemoryStream();
        foreach (var payload in payloads)
        {
            MidgeDiskFormat.WriteUInt32(stream, checked((uint)payload.Length));
            MidgeDiskFormat.WriteUInt32(stream, MidgeDiskFormat.Crc32C(payload));
            stream.Write(payload);
        }

        return stream.ToArray();
    }
}
