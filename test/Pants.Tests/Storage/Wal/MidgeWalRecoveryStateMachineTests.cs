namespace Cntryl.Pants.Tests;

public sealed class MidgeWalRecoveryStateMachineTests
{
    [Fact]
    public void ShouldApplySplitMutationsAtCommitSequenceGivenMatchingCommit()
    {
        using var recovery = new MidgeWalRecoveryStateMachine();
        var applied = new List<(MidgeWalMutation Mutation, ulong CommitSequence)>();
        var mutation = CreatePut("alpha", "one", sequence: 2);

        Visit(
            recovery,
            applied,
            MidgeWalCodec.EncodeTransactionMarker(
                MidgeWalOperation.TransactionBegin,
                transactionId: 9,
                sequence: 1,
                writerEpoch: 7),
            MidgeWalCodec.EncodeTransactionMutation(
                mutation,
                transactionId: 9,
                writerEpoch: 7));

        Assert.Empty(applied);

        Visit(
            recovery,
            applied,
            MidgeWalCodec.EncodeTransactionMarker(
                MidgeWalOperation.TransactionCommit,
                transactionId: 9,
                sequence: 3,
                writerEpoch: 7));

        var result = Assert.Single(applied);
        Assert.Equal("alpha"u8.ToArray(), result.Mutation.Key);
        Assert.Equal("one"u8.ToArray(), result.Mutation.Value);
        Assert.Equal(2UL, result.Mutation.Sequence);
        Assert.Equal(3UL, result.CommitSequence);
    }

    [Fact]
    public void ShouldDiscardSplitMutationsGivenMissingCommit()
    {
        var applied = new List<(MidgeWalMutation Mutation, ulong CommitSequence)>();
        using (var recovery = new MidgeWalRecoveryStateMachine())
        {
            Visit(
                recovery,
                applied,
                MidgeWalCodec.EncodeTransactionMarker(
                    MidgeWalOperation.TransactionBegin,
                    transactionId: 9,
                    sequence: 1,
                    writerEpoch: 7),
                MidgeWalCodec.EncodeTransactionMutation(
                    CreatePut("alpha", "one", sequence: 2),
                    transactionId: 9,
                    writerEpoch: 7));
        }

        Assert.Empty(applied);
    }

    [Fact]
    public void ShouldRejectDuplicateBeginGivenOpenTransactionWithSameEpochAndId()
    {
        using var recovery = new MidgeWalRecoveryStateMachine();
        var applied = new List<(MidgeWalMutation Mutation, ulong CommitSequence)>();
        var begin = MidgeWalCodec.EncodeTransactionMarker(
            MidgeWalOperation.TransactionBegin,
            transactionId: 9,
            sequence: 1,
            writerEpoch: 7);
        Visit(recovery, applied, begin);

        Assert.Throws<PantsStorageException>(() => Visit(recovery, applied, begin));
    }

    [Fact]
    public void ShouldIsolateOpenTransactionsByWriterEpochGivenSameTransactionId()
    {
        using var recovery = new MidgeWalRecoveryStateMachine();
        var applied = new List<(MidgeWalMutation Mutation, ulong CommitSequence)>();

        Visit(
            recovery,
            applied,
            MidgeWalCodec.EncodeTransactionMarker(
                MidgeWalOperation.TransactionBegin,
                transactionId: 9,
                sequence: 1,
                writerEpoch: 7),
            MidgeWalCodec.EncodeTransactionMutation(
                CreatePut("epoch-seven", "one", sequence: 2),
                transactionId: 9,
                writerEpoch: 7),
            MidgeWalCodec.EncodeTransactionMarker(
                MidgeWalOperation.TransactionBegin,
                transactionId: 9,
                sequence: 10,
                writerEpoch: 8),
            MidgeWalCodec.EncodeTransactionMutation(
                CreatePut("epoch-eight", "two", sequence: 11),
                transactionId: 9,
                writerEpoch: 8));

        Assert.Empty(applied);

        Visit(
            recovery,
            applied,
            MidgeWalCodec.EncodeTransactionMarker(
                MidgeWalOperation.TransactionCommit,
                transactionId: 9,
                sequence: 12,
                writerEpoch: 8));

        var first = Assert.Single(applied);
        Assert.Equal("epoch-eight"u8.ToArray(), first.Mutation.Key);
        Assert.Equal(12UL, first.CommitSequence);

        Visit(
            recovery,
            applied,
            MidgeWalCodec.EncodeTransactionMarker(
                MidgeWalOperation.TransactionCommit,
                transactionId: 9,
                sequence: 3,
                writerEpoch: 7));

        Assert.Equal(2, applied.Count);
        Assert.Equal("epoch-seven"u8.ToArray(), applied[1].Mutation.Key);
        Assert.Equal(3UL, applied[1].CommitSequence);
    }

    [Fact]
    public void ShouldApplyTaggedMutationAsStandaloneGivenNoMatchingBegin()
    {
        using var recovery = new MidgeWalRecoveryStateMachine();
        var applied = new List<(MidgeWalMutation Mutation, ulong CommitSequence)>();

        Visit(
            recovery,
            applied,
            MidgeWalCodec.EncodeTransactionMutation(
                CreatePut("standalone", "value", sequence: 14),
                transactionId: 9,
                writerEpoch: 7));

        var result = Assert.Single(applied);
        Assert.Equal("standalone"u8.ToArray(), result.Mutation.Key);
        Assert.Equal(14UL, result.CommitSequence);
    }

    [Fact]
    public void ShouldApplyAtomicBatchGivenDirectTransactionRecord()
    {
        using var recovery = new MidgeWalRecoveryStateMachine();
        var applied = new List<(MidgeWalMutation Mutation, ulong CommitSequence)>();
        var batch = MidgeWalCodec.EncodeTransactionBatch(
            transactionId: 9,
            beginSequence: 20,
            writerEpoch: 7,
            [
                CreatePut("alpha", "one", sequence: 0),
                CreatePut("bravo", "two", sequence: 0)
            ]);

        Visit(recovery, applied, batch);

        Assert.Collection(
            applied,
            first =>
            {
                Assert.Equal("alpha"u8.ToArray(), first.Mutation.Key);
                Assert.Equal(21UL, first.Mutation.Sequence);
                Assert.Equal(23UL, first.CommitSequence);
            },
            second =>
            {
                Assert.Equal("bravo"u8.ToArray(), second.Mutation.Key);
                Assert.Equal(22UL, second.Mutation.Sequence);
                Assert.Equal(23UL, second.CommitSequence);
            });
    }

    static MidgeWalMutation CreatePut(string key, string value, ulong sequence) => new(
        ColumnFamilyId: 0,
        MidgeWalOperation.Put,
        TestBytes.FromString(key),
        TestBytes.FromString(value),
        sequence,
        Expiration: null,
        RangeEnd: null);

    static void Visit(
        MidgeWalRecoveryStateMachine recovery,
        List<(MidgeWalMutation Mutation, ulong CommitSequence)> applied,
        params byte[][] payloads)
    {
        var bytes = Frame(payloads);
        MidgeWalFrameReader.Visit(
            bytes,
            (record, _) => recovery.Accept(
                record,
                (mutation, commitSequence) => applied.Add((mutation, commitSequence))));
    }

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
