using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class WalRecordSemanticsTests
{
    [Theory]
    [InlineData((byte)WalOperation.Put)]
    [InlineData((byte)WalOperation.Insert)]
    [InlineData((byte)WalOperation.TransactionBatch)]
    public void ShouldRejectMissingValueBeforeReplayingRecord(byte operationCode) =>
        AssertRejected(new WalRecord(0, (WalOperation)operationCode, "key"u8.ToArray(), null, 1, null, null, 1, 1));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024)]
    public void ShouldRejectValueBearingDeleteBeforeReplayingRecord(int valueBytes) =>
        AssertRejected(new WalRecord(0, WalOperation.Delete, "key"u8.ToArray(), new byte[valueBytes],
            1, null, null, null, 1));

    [Fact]
    public void ShouldRejectMissingRangeEndBeforeReplayingRecord() =>
        AssertRejected(new WalRecord(0, WalOperation.DeleteRange, "key"u8.ToArray(), null,
            1, null, null, null, 1));

    [Theory]
    [InlineData((byte)WalOperation.TransactionBegin)]
    [InlineData((byte)WalOperation.TransactionCommit)]
    [InlineData((byte)WalOperation.TransactionBatch)]
    public void ShouldRejectMissingTransactionIdentityBeforeReplayingMarker(byte operationCode) =>
        AssertRejected(new WalRecord(0, (WalOperation)operationCode, "txn"u8.ToArray(),
            (WalOperation)operationCode == WalOperation.TransactionBatch ? [] : null, 1, null, null, null, 1));

    [Theory]
    [InlineData((byte)WalOperation.Put)]
    [InlineData((byte)WalOperation.Insert)]
    [InlineData((byte)WalOperation.Delete)]
    [InlineData((byte)WalOperation.DeleteRange)]
    public void ShouldRejectMalformedNestedOperationBeforeApplyingBatch(byte operationCode)
    {
        using var directory = new TemporaryDirectory();
        var operation = (WalOperation)operationCode;
        var value = operation == WalOperation.Delete ? "invalid"u8.ToArray() : null;
        var payload = WalCodec.EncodeTransactionBatch(1, 1, 1,
            [new WalMutation(0, operation, "key"u8.ToArray(), value, 2, null, null)]);
        var record = WalCodec.DecodeRecord(payload, segmentEpoch: 1);
        using var recovery = new WalRecoveryStateMachine(Path.Combine(directory.Path, "recovery"));
        var applied = 0;

        Assert.Throws<StorageException>(() => recovery.Accept(record, (_, _) => applied++));

        Assert.Equal(0, applied);
    }

    static void AssertRejected(WalRecord record)
    {
        using var directory = new TemporaryDirectory();
        var ledger = WalLedgerStore.LoadOrCreate(directory.Path, 1);
        using var stream = new FileStream(ledger.ActivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        WalCodec.AppendFrame(stream.SafeFileHandle, stream.Length, WalCodec.EncodeRecord(record));
        stream.Position = 0;
        var replayed = 0;

        Assert.Throws<PantsCorruptionException>(() =>
            WalFrameReader.Visit(stream, (_, _) => replayed++));

        Assert.Equal(0, replayed);
    }
}
