using System.Buffers.Binary;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class WalConfiguredReplayBudgetTests
{
    [Fact]
    public void ShouldRefuseAnOversizedRecoveryFrameBeforeReadingItsBody()
    {
        using var stream = new HeaderOnlyFrameStream(64);
        Assert.Throws<PantsResourceLimitException>(() => WalFrameReader.VisitRecovery(
            stream, static (_, _) => { }, recoveryWork: null, cancellationToken: default,
            maximumReplayableTransactionBytes: 32));
        Assert.False(stream.BodyRead);
    }

    [Fact]
    public void ShouldRefuseAnOversizedLz4DeclaredValueBeforeDecodingItsInvalidBody()
    {
        var compressed = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(compressed, 4096);
        compressed[4] = 0xff;
        var payload = CompressedRecord(compressed, 1);
        Assert.Throws<PantsResourceLimitException>(() => WalCodec.DecodeRecord(
            payload, segmentEpoch: 0, maximumDecodedBytes: 144 + 1 + 4096 - 1));
        var decodeFailure = Assert.ThrowsAny<PantsException>(() => WalCodec.DecodeRecord(
            payload, segmentEpoch: 0, maximumDecodedBytes: 144 + 1 + 4096));
        Assert.IsNotType<PantsResourceLimitException>(decodeFailure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldBoundKnownAndUnknownZstdValuesBeforeDecompression(bool includeContentSize)
    {
        var value = Enumerable.Repeat((byte)'z', 32).ToArray();
        var payload = CompressedRecord(RawZstdFrame(value, includeContentSize), 2);
        var bound = 144L + 1 + (includeContentSize ? value.Length : 64 * 1024);
        Assert.Throws<PantsResourceLimitException>(() => WalCodec.DecodeRecord(
            payload, segmentEpoch: 0, maximumDecodedBytes: bound - 1));
        var record = WalCodec.DecodeRecord(payload, segmentEpoch: 0, maximumDecodedBytes: bound);
        Assert.Equal(value, record.Value);
    }

    [Fact]
    public void ShouldApplyAnAtomicBatchOnlyWhenItsNativeDecodedAllocationBoundFits()
    {
        using var directory = new TemporaryDirectory();
        var record = WalCodec.DecodeRecord(WalCodec.EncodeTransactionBatch(9, 1, 7,
            [Mutation(2, 32), Mutation(3, 32)]), segmentEpoch: 7);
        var bound = 2L * record.Value!.Length + 416L * 2;
        var applied = new List<WalMutation>();
        using (var tooSmall = new WalRecoveryStateMachine(directory.Path,
                   ledger: null, maximumReplayableTransactionBytes: bound - 1))
        {
            Assert.Throws<PantsResourceLimitException>(() => tooSmall.Accept(record, (mutation, _) => applied.Add(mutation)));
            Assert.Empty(applied);
        }
        using var exact = new WalRecoveryStateMachine(directory.Path,
            ledger: null, maximumReplayableTransactionBytes: bound);
        exact.Accept(record, (mutation, _) => applied.Add(mutation));
        Assert.Equal(2, applied.Count);
    }

    [Fact]
    public void ShouldShareThePendingLimitAcrossSplitTransactionsAndReclaimItAtCommit()
    {
        using var directory = new TemporaryDirectory();
        using var recovery = new WalRecoveryStateMachine(directory.Path,
            ledger: null, maximumReplayableTransactionBytes: 1024);
        var applied = new List<WalMutation>();
        Action<WalMutation, ulong> apply = (mutation, _) => applied.Add(mutation);
        recovery.Accept(Marker(WalOperation.TransactionBegin, 1, 1, 7), apply);
        recovery.Accept(Member(1, 2, 7), apply);
        recovery.Accept(Marker(WalOperation.TransactionBegin, 2, 3, 7), apply);
        Assert.Throws<PantsResourceLimitException>(() => recovery.Accept(Member(2, 4, 7), apply));
        Assert.Empty(applied);
        recovery.Accept(Marker(WalOperation.TransactionCommit, 1, 5, 7), apply);
        recovery.Accept(Member(2, 6, 7), apply);
        recovery.Accept(Marker(WalOperation.TransactionCommit, 2, 7, 7), apply);
        Assert.Equal(2, applied.Count);
        Assert.False(recovery.HasOpenTransactions);
    }

    [Fact]
    public void ShouldReclaimAnIncompletePredecessorUnitWhenTheWriterEpochChanges()
    {
        using var directory = new TemporaryDirectory();
        using var recovery = new WalRecoveryStateMachine(directory.Path,
            ledger: null, maximumReplayableTransactionBytes: 1024);
        var applied = new List<WalMutation>();
        Action<WalMutation, ulong> apply = (mutation, _) => applied.Add(mutation);
        recovery.Accept(Marker(WalOperation.TransactionBegin, 1, 1, 7), apply);
        recovery.Accept(Member(1, 2, 7), apply);
        recovery.Accept(Marker(WalOperation.TransactionBegin, 1, 3, 8), apply);
        recovery.Accept(Member(1, 4, 8), apply);
        recovery.Accept(Marker(WalOperation.TransactionCommit, 1, 5, 8), apply);
        Assert.Single(applied);
        Assert.Equal(4UL, applied[0].Sequence);
        Assert.False(recovery.HasOpenTransactions);
    }

    [Fact]
    public void ShouldKeepLocalReplayUnboundedWhenNoConfiguredLimitIsSupplied()
    {
        using var directory = new TemporaryDirectory();
        using var recovery = new WalRecoveryStateMachine(directory.Path,
            ledger: null, maximumReplayableTransactionBytes: null);
        var applied = new List<WalMutation>();
        Action<WalMutation, ulong> apply = (mutation, _) => applied.Add(mutation);
        recovery.Accept(Marker(WalOperation.TransactionBegin, 1, 1, 7), apply);
        recovery.Accept(Member(1, 2, 7), apply);
        recovery.Accept(Marker(WalOperation.TransactionBegin, 2, 3, 7), apply);
        recovery.Accept(Member(2, 4, 7), apply);
        Assert.Empty(applied);
        recovery.Accept(Marker(WalOperation.TransactionCommit, 1, 5, 7), apply);
        recovery.Accept(Marker(WalOperation.TransactionCommit, 2, 6, 7), apply);
        Assert.Equal(2, applied.Count);
        Assert.False(recovery.HasOpenTransactions);
    }

    [Fact]
    public void ShouldKeepUnconfiguredRecordDecodingAtTheExistingLocalLimit()
    {
        var value = Enumerable.Repeat((byte)'v', 1024 * 1024).ToArray();
        var payload = WalCodec.EncodeRecord(new WalRecord(0, WalOperation.Put, "key"u8.ToArray(),
            value, 1, null, null, null, 7));
        Assert.Throws<PantsResourceLimitException>(() => WalCodec.DecodeRecord(payload, 7, 512 * 1024));
        Assert.Equal(value, WalCodec.DecodeRecord(payload, 7).Value);
    }

    static WalMutation Mutation(ulong sequence, int valueBytes) =>
        new(0, WalOperation.Put, [(byte)sequence], new byte[valueBytes], sequence, null, null);

    static WalRecord Member(ulong transaction, ulong sequence, ulong epoch) =>
        WalCodec.DecodeRecord(WalCodec.EncodeTransactionMutation(Mutation(sequence, 400), transaction, epoch), epoch);

    static WalRecord Marker(WalOperation operation, ulong transaction, ulong sequence, ulong epoch) =>
        WalCodec.DecodeRecord(WalCodec.EncodeTransactionMarker(operation, transaction, sequence, epoch), epoch);

    static byte[] CompressedRecord(byte[] compressed, byte algorithm)
    {
        using var stream = new MemoryStream();
        stream.Write("MW"u8);
        stream.WriteByte(2);
        Tlv(stream, 1, [(byte)WalOperation.Put]);
        var family = new byte[4];
        Tlv(stream, 2, family);
        var sequence = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(sequence, 1);
        Tlv(stream, 3, sequence);
        Tlv(stream, 4, "k"u8);
        Tlv(stream, 5, compressed);
        Tlv(stream, 9, [algorithm]);
        return stream.ToArray();
    }

    static void Tlv(Stream stream, byte tag, ReadOnlySpan<byte> value)
    {
        stream.WriteByte(tag);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    static byte[] RawZstdFrame(byte[] value, bool includeContentSize)
    {
        using var frame = new MemoryStream();
        frame.Write([0x28, 0xb5, 0x2f, 0xfd]);
        frame.WriteByte(includeContentSize ? (byte)0x20 : (byte)0x00);
        // Single-segment frames carry one-byte content size; unknown-size frames
        // carry a window descriptor of zero (1 KiB), enough for this raw block.
        frame.WriteByte(includeContentSize ? (byte)value.Length : (byte)0x00);
        var block = ((uint)value.Length << 3) | 1;
        frame.WriteByte((byte)block);
        frame.WriteByte((byte)(block >> 8));
        frame.WriteByte((byte)(block >> 16));
        frame.Write(value);
        return frame.ToArray();
    }

    sealed class HeaderOnlyFrameStream : MemoryStream
    {
        readonly long _length;
        public HeaderOnlyFrameStream(uint payloadLength) : base(Header(payloadLength), writable: false) =>
            _length = payloadLength + 8L;
        public bool BodyRead { get; private set; }
        public override long Length => _length;
        public override int Read(Span<byte> buffer)
        {
            if (Position >= 8)
            {
                BodyRead = true;
                throw new IOException("The configured frame limit must be checked before its body is read.");
            }
            var count = (int)Math.Min(buffer.Length, 8 - Position);
            Header(64).AsSpan((int)Position, count).CopyTo(buffer);
            Position += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        static byte[] Header(uint payloadLength)
        {
            var header = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(header, payloadLength);
            return header;
        }
    }
}
