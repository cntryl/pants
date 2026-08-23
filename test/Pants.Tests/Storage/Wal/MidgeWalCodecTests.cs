namespace Pants.Tests;

public sealed class MidgeWalCodecTests
{
    [Fact]
    public void ShouldRejectTransactionBatchGivenEmptyOperationCount()
    {
        var record = CreateBatch(operationCount: 0, []);

        var exception = Assert.Throws<PantsStorageException>(() =>
            MidgeWalCodec.DecodeTransactionBatch(record, out _, out _));

        Assert.Contains("empty operation count", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRejectTransactionBatchBeforeAllocationGivenHugeCountAndNoRecords()
    {
        var record = CreateBatch(operationCount: 10_000_000, []);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var exception = Assert.Throws<PantsStorageException>(() =>
            MidgeWalCodec.DecodeTransactionBatch(record, out _, out _));
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Contains(
            "operation count exceeds remaining bytes",
            exception.Message,
            StringComparison.Ordinal);
        Assert.InRange(allocatedBytes, 0L, 1_048_576L);
    }

    [Fact]
    public void ShouldRejectTransactionBatchBeforeDecodingGivenRecordBelowExactMinimumLength()
    {
        var record = CreateBatch(operationCount: 1, new byte[19]);

        var exception = Assert.Throws<PantsStorageException>(() =>
            MidgeWalCodec.DecodeTransactionBatch(record, out _, out _));

        Assert.Contains(
            "operation count exceeds remaining bytes",
            exception.Message,
            StringComparison.Ordinal);
    }

    static MidgeWalRecord CreateBatch(uint operationCount, byte[] records)
    {
        const ulong transactionId = 7;
        const ulong beginSequence = 11;
        var commitSequence = beginSequence + operationCount + 1;
        using var batch = new MemoryStream();
        batch.Write("TB"u8);
        batch.WriteByte(1);
        MidgeDiskFormat.WriteUInt64(batch, transactionId);
        MidgeDiskFormat.WriteUInt64(batch, beginSequence);
        MidgeDiskFormat.WriteUInt64(batch, commitSequence);
        MidgeDiskFormat.WriteUInt32(batch, operationCount);
        batch.Write(records);

        return new MidgeWalRecord(
            ColumnFamilyId: 0,
            MidgeWalOperation.TransactionBatch,
            "txn"u8.ToArray(),
            batch.ToArray(),
            commitSequence,
            Expiration: null,
            RangeEnd: null,
            transactionId,
            WriterEpoch: 9);
    }
}
