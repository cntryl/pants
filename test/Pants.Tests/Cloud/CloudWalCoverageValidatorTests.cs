namespace Cntryl.Pants.Tests;

public sealed class CloudWalCoverageValidatorTests
{
    [Fact]
    public void ShouldAcceptWalMutationGivenManifestFileCoversSequenceAndKey()
    {
        var bytes = CreateWalBytes(MidgeWalOperation.Put, "middle"u8.ToArray());
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        CloudWalCoverageValidator.ValidateAndEnsureCovered(
            bytes,
            expectedMaximumSequence: 3,
            expectedWriterEpoch: 7,
            manifest);
    }

    [Fact]
    public void ShouldRejectWalMutationGivenKeyOutsideManifestFileRange()
    {
        var bytes = CreateWalBytes(MidgeWalOperation.Put, "zulu-plus"u8.ToArray());
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        Assert.Throws<PantsCorruptionException>(() =>
            CloudWalCoverageValidator.ValidateAndEnsureCovered(
                bytes,
                expectedMaximumSequence: 3,
                expectedWriterEpoch: 7,
                manifest));
    }

    [Fact]
    public void ShouldReportValidWalAsUncoveredGivenKeyOutsideManifestFileRange()
    {
        var bytes = CreateWalBytes(MidgeWalOperation.Put, "zulu-plus"u8.ToArray());
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        var covered = CloudWalCoverageValidator.ValidateAndIsCovered(
            bytes,
            expectedMaximumSequence: 3,
            expectedWriterEpoch: 7,
            manifest);

        Assert.False(covered);
    }

    [Fact]
    public void ShouldRejectWalRangeTombstoneGivenEndOutsideManifestFileRange()
    {
        var bytes = CreateWalBytes(
            MidgeWalOperation.DeleteRange,
            "middle"u8.ToArray(),
            "zulu-plus"u8.ToArray());
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        Assert.Throws<PantsCorruptionException>(() =>
            CloudWalCoverageValidator.ValidateAndEnsureCovered(
                bytes,
                expectedMaximumSequence: 3,
                expectedWriterEpoch: 7,
                manifest));
    }

    [Fact]
    public void ShouldRejectWalGivenFrameWriterEpochDiffersFromCatalog()
    {
        var bytes = CreateWalBytes(MidgeWalOperation.Put, "middle"u8.ToArray());
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        Assert.Throws<PantsCorruptionException>(() =>
            CloudWalCoverageValidator.ValidateAndEnsureCovered(
                bytes,
                expectedMaximumSequence: 3,
                expectedWriterEpoch: 8,
                manifest));
    }

    [Fact]
    public void ShouldAcceptSplitWalMutationGivenManifestFileCoversSequenceAndKey()
    {
        var bytes = CreateSplitWalBytes("middle"u8.ToArray());
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        CloudWalCoverageValidator.ValidateAndEnsureCovered(
            bytes,
            expectedMaximumSequence: 3,
            expectedWriterEpoch: 7,
            manifest);
    }

    [Fact]
    public void ShouldReportSplitWalAsUncoveredGivenMutationOutsideManifestFileRange()
    {
        var bytes = CreateSplitWalBytes("zulu-plus"u8.ToArray());
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        var covered = CloudWalCoverageValidator.ValidateAndIsCovered(
            bytes,
            expectedMaximumSequence: 3,
            expectedWriterEpoch: 7,
            manifest);

        Assert.False(covered);
    }

    [Theory]
    [InlineData((byte)MidgeWalOperation.Put)]
    [InlineData((byte)MidgeWalOperation.Insert)]
    [InlineData((byte)MidgeWalOperation.DeleteRange)]
    public void ShouldRejectMalformedStandaloneWalMutationGivenRequiredFieldIsMissing(
        byte operation)
    {
        var bytes = Frame(CreateMalformedMutationPayload(
            (MidgeWalOperation)operation,
            transactionId: null));
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        Assert.Throws<PantsCorruptionException>(() =>
            CloudWalCoverageValidator.ValidateAndEnsureCovered(
                bytes,
                expectedMaximumSequence: 3,
                expectedWriterEpoch: 7,
                manifest));
    }

    [Theory]
    [InlineData((byte)MidgeWalOperation.Put)]
    [InlineData((byte)MidgeWalOperation.Insert)]
    [InlineData((byte)MidgeWalOperation.DeleteRange)]
    public void ShouldRejectMalformedSplitWalMutationGivenRequiredFieldIsMissing(
        byte operation)
    {
        var bytes = Frame(
            MidgeWalCodec.EncodeTransactionMarker(
                MidgeWalOperation.TransactionBegin,
                transactionId: 1,
                sequence: 1,
                writerEpoch: 7),
            CreateMalformedMutationPayload((MidgeWalOperation)operation, transactionId: 1),
            MidgeWalCodec.EncodeTransactionMarker(
                MidgeWalOperation.TransactionCommit,
                transactionId: 1,
                sequence: 3,
                writerEpoch: 7));
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        Assert.Throws<PantsCorruptionException>(() =>
            CloudWalCoverageValidator.ValidateAndEnsureCovered(
                bytes,
                expectedMaximumSequence: 3,
                expectedWriterEpoch: 7,
                manifest));
    }

    static byte[] CreateWalBytes(
        MidgeWalOperation operation,
        byte[] key,
        byte[]? rangeEnd = null)
    {
        var payload = MidgeWalCodec.EncodeTransactionBatch(
            transactionId: 1,
            beginSequence: 1,
            writerEpoch: 7,
            [new MidgeWalMutation(
                ColumnFamilyId: 0,
                operation,
                key,
                operation == MidgeWalOperation.Put ? "value"u8.ToArray() : null,
                Sequence: 2,
                Expiration: null,
                rangeEnd)]);
        return Frame(payload);
    }

    static byte[] CreateSplitWalBytes(byte[] key) => Frame(
        MidgeWalCodec.EncodeTransactionMarker(
            MidgeWalOperation.TransactionBegin,
            transactionId: 1,
            sequence: 1,
            writerEpoch: 7),
        MidgeWalCodec.EncodeTransactionMutation(
            new MidgeWalMutation(
                ColumnFamilyId: 0,
                MidgeWalOperation.Put,
                key,
                "value"u8.ToArray(),
                Sequence: 2,
                Expiration: null,
                RangeEnd: null),
            transactionId: 1,
            writerEpoch: 7),
        MidgeWalCodec.EncodeTransactionMarker(
            MidgeWalOperation.TransactionCommit,
            transactionId: 1,
            sequence: 3,
            writerEpoch: 7));

    static byte[] CreateMalformedMutationPayload(
        MidgeWalOperation operation,
        ulong? transactionId) =>
        MidgeWalCodec.EncodeRecord(new MidgeWalRecord(
            ColumnFamilyId: 0,
            operation,
            "middle"u8.ToArray(),
            Value: null,
            Sequence: transactionId.HasValue ? 2UL : 3UL,
            Expiration: null,
            RangeEnd: null,
            transactionId,
            WriterEpoch: 7));

    static byte[] Frame(params byte[][] payloads)
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

    static MidgeManifest CreateManifest(byte[] smallestKey, byte[] largestKey) => new()
    {
        LastPersistedSequence = 3,
        Files =
        [
            new MidgeFileMeta
            {
                Name = "00000000000000000001.sst",
                ColumnFamilyId = 0,
                SmallestKey = smallestKey.Select(static value => (int)value).ToArray(),
                LargestKey = largestKey.Select(static value => (int)value).ToArray(),
                SmallestSequence = 2,
                LargestSequence = 2
            }
        ]
    };
}
