namespace Cntryl.Pants.Tests.Storage.Wal;

public sealed class WalRecoveryStateMachineTests
{
    [Fact]
    public void ShouldApplySplitMutationsAtCommitSequenceGivenMatchingCommit()
    {
        using var recovery = new WalRecoveryStateMachine();
        var applied = new List<(WalMutation Mutation, ulong CommitSequence)>();
        var mutation = CreatePut("alpha", "one", 2);

        Visit(
            recovery,
            applied,
            WalCodec.EncodeTransactionMarker(
                WalOperation.TransactionBegin,
                9,
                1,
                7),
            WalCodec.EncodeTransactionMutation(
                mutation,
                9,
                7));

        Assert.Empty(applied);

        Visit(
            recovery,
            applied,
            WalCodec.EncodeTransactionMarker(
                WalOperation.TransactionCommit,
                9,
                3,
                7));

        var result = Assert.Single(applied);
        Assert.Equal("alpha"u8.ToArray(), result.Mutation.Key);
        Assert.Equal("one"u8.ToArray(), result.Mutation.Value);
        Assert.Equal(2UL, result.Mutation.Sequence);
        Assert.Equal(3UL, result.CommitSequence);
    }

    [Fact]
    public void ShouldDiscardSplitMutationsGivenMissingCommit()
    {
        var applied = new List<(WalMutation Mutation, ulong CommitSequence)>();
        using (var recovery = new WalRecoveryStateMachine())
        {
            Visit(
                recovery,
                applied,
                WalCodec.EncodeTransactionMarker(
                    WalOperation.TransactionBegin,
                    9,
                    1,
                    7),
                WalCodec.EncodeTransactionMutation(
                    CreatePut("alpha", "one", 2),
                    9,
                    7));
        }

        Assert.Empty(applied);
    }

    [Fact]
    public void ShouldRejectDuplicateBeginGivenOpenTransactionWithSameEpochAndId()
    {
        using var recovery = new WalRecoveryStateMachine();
        var applied = new List<(WalMutation Mutation, ulong CommitSequence)>();
        var begin = WalCodec.EncodeTransactionMarker(
            WalOperation.TransactionBegin,
            9,
            1,
            7);
        Visit(recovery, applied, begin);

        Assert.Throws<StorageException>(() => Visit(recovery, applied, begin));
    }

    [Fact]
    public void ShouldIsolateOpenTransactionsByWriterEpochGivenSameTransactionId()
    {
        using var recovery = new WalRecoveryStateMachine();
        var applied = new List<(WalMutation Mutation, ulong CommitSequence)>();

        Visit(
            recovery,
            applied,
            WalCodec.EncodeTransactionMarker(
                WalOperation.TransactionBegin,
                9,
                1,
                7),
            WalCodec.EncodeTransactionMutation(
                CreatePut("epoch-seven", "one", 2),
                9,
                7),
            WalCodec.EncodeTransactionMarker(
                WalOperation.TransactionBegin,
                9,
                10,
                8),
            WalCodec.EncodeTransactionMutation(
                CreatePut("epoch-eight", "two", 11),
                9,
                8));

        Assert.Empty(applied);

        Visit(
            recovery,
            applied,
            WalCodec.EncodeTransactionMarker(
                WalOperation.TransactionCommit,
                9,
                12,
                8));

        var first = Assert.Single(applied);
        Assert.Equal("epoch-eight"u8.ToArray(), first.Mutation.Key);
        Assert.Equal(12UL, first.CommitSequence);

        Visit(
            recovery,
            applied,
            WalCodec.EncodeTransactionMarker(
                WalOperation.TransactionCommit,
                9,
                3,
                7));

        Assert.Equal(2, applied.Count);
        Assert.Equal("epoch-seven"u8.ToArray(), applied[1].Mutation.Key);
        Assert.Equal(3UL, applied[1].CommitSequence);
    }

    [Fact]
    public void ShouldApplyTaggedMutationAsStandaloneGivenNoMatchingBegin()
    {
        using var recovery = new WalRecoveryStateMachine();
        var applied = new List<(WalMutation Mutation, ulong CommitSequence)>();

        Visit(
            recovery,
            applied,
            WalCodec.EncodeTransactionMutation(
                CreatePut("standalone", "value", 14),
                9,
                7));

        var result = Assert.Single(applied);
        Assert.Equal("standalone"u8.ToArray(), result.Mutation.Key);
        Assert.Equal(14UL, result.CommitSequence);
    }

    [Fact]
    public void ShouldApplyAtomicBatchGivenDirectTransactionRecord()
    {
        using var recovery = new WalRecoveryStateMachine();
        var applied = new List<(WalMutation Mutation, ulong CommitSequence)>();
        var batch = WalCodec.EncodeTransactionBatch(
            9,
            20,
            7,
            [
                CreatePut("alpha", "one", 0),
                CreatePut("bravo", "two", 0)
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

    static WalMutation CreatePut(string key, string value, ulong sequence) => new(
        0,
        WalOperation.Put,
        TestBytes.FromString(key),
        TestBytes.FromString(value),
        sequence,
        null,
        null);

    static void Visit(
        WalRecoveryStateMachine recovery,
        List<(WalMutation Mutation, ulong CommitSequence)> applied,
        params byte[][] payloads)
    {
        var bytes = Frame(payloads);
        WalFrameReader.Visit(
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
            DiskFormat.WriteUInt32(stream, checked((uint)payload.Length));
            DiskFormat.WriteUInt32(stream, DiskFormat.Crc32C(payload));
            stream.Write(payload);
        }

        return stream.ToArray();
    }
}
