namespace Cntryl.Pants.Tests.Cloud;

public sealed class CloudWalCoverageValidatorTests
{
    [Fact]
    public void ShouldAcceptWalMutationGivenManifestFileCoversSequenceAndKey()
    {
        var bytes = CreateWalBytes(WalOperation.Put, "middle"u8.ToArray());
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        CloudWalCoverageValidator.ValidateAndEnsureCovered(
            bytes,
            3,
            7,
            manifest);
    }

    [Fact]
    public void ShouldRejectWalMutationGivenKeyOutsideManifestFileRange()
    {
        var bytes = CreateWalBytes(WalOperation.Put, "zulu-plus"u8.ToArray());
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        Assert.Throws<PantsCorruptionException>(() =>
            CloudWalCoverageValidator.ValidateAndEnsureCovered(
                bytes,
                3,
                7,
                manifest));
    }

    [Fact]
    public void ShouldReportValidWalAsUncoveredGivenKeyOutsideManifestFileRange()
    {
        var bytes = CreateWalBytes(WalOperation.Put, "zulu-plus"u8.ToArray());
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        var covered = CloudWalCoverageValidator.ValidateAndIsCovered(
            bytes,
            3,
            7,
            manifest);

        Assert.False(covered);
    }

    [Fact]
    public void ShouldRejectWalRangeTombstoneGivenEndOutsideManifestFileRange()
    {
        var bytes = CreateWalBytes(
            WalOperation.DeleteRange,
            "middle"u8.ToArray(),
            "zulu-plus"u8.ToArray());
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        Assert.Throws<PantsCorruptionException>(() =>
            CloudWalCoverageValidator.ValidateAndEnsureCovered(
                bytes,
                3,
                7,
                manifest));
    }

    [Fact]
    public void ShouldRejectWalGivenFrameWriterEpochDiffersFromCatalog()
    {
        var bytes = CreateWalBytes(WalOperation.Put, "middle"u8.ToArray());
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        Assert.Throws<PantsCorruptionException>(() =>
            CloudWalCoverageValidator.ValidateAndEnsureCovered(
                bytes,
                3,
                8,
                manifest));
    }

    [Fact]
    public void ShouldAcceptSplitWalMutationGivenManifestFileCoversSequenceAndKey()
    {
        var bytes = CreateSplitWalBytes("middle"u8.ToArray());
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        CloudWalCoverageValidator.ValidateAndEnsureCovered(
            bytes,
            3,
            7,
            manifest);
    }

    [Fact]
    public void ShouldReportSplitWalAsUncoveredGivenMutationOutsideManifestFileRange()
    {
        var bytes = CreateSplitWalBytes("zulu-plus"u8.ToArray());
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        var covered = CloudWalCoverageValidator.ValidateAndIsCovered(
            bytes,
            3,
            7,
            manifest);

        Assert.False(covered);
    }

    [Theory]
    [InlineData((byte)WalOperation.Put)]
    [InlineData((byte)WalOperation.DeleteRange)]
    public void ShouldRejectMalformedStandaloneWalMutationGivenRequiredFieldIsMissing(
        byte operation)
    {
        var bytes = Frame(CreateMalformedMutationPayload(
            (WalOperation)operation,
            null));
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        Assert.Throws<PantsCorruptionException>(() =>
            CloudWalCoverageValidator.ValidateAndEnsureCovered(
                bytes,
                3,
                7,
                manifest));
    }

    [Theory]
    [InlineData((byte)WalOperation.Put)]
    [InlineData((byte)WalOperation.DeleteRange)]
    public void ShouldRejectMalformedSplitWalMutationGivenRequiredFieldIsMissing(
        byte operation)
    {
        var bytes = Frame(
            WalCodec.EncodeTransactionMarker(
                WalOperation.TransactionBegin,
                1,
                1,
                7),
            CreateMalformedMutationPayload((WalOperation)operation, 1),
            WalCodec.EncodeTransactionMarker(
                WalOperation.TransactionCommit,
                1,
                3,
                7));
        var manifest = CreateManifest("alpha"u8.ToArray(), "zulu"u8.ToArray());

        Assert.Throws<PantsCorruptionException>(() =>
            CloudWalCoverageValidator.ValidateAndEnsureCovered(
                bytes,
                3,
                7,
                manifest));
    }

    static byte[] CreateWalBytes(
        WalOperation operation,
        byte[] key,
        byte[]? rangeEnd = null)
    {
        var payload = WalCodec.EncodeTransactionBatch(
            1,
            1,
            7,
            [
                new WalMutation(
                    0,
                    operation,
                    key,
                    operation == WalOperation.Put ? "value"u8.ToArray() : null,
                    2,
                    null,
                    rangeEnd)
            ]);
        return Frame(payload);
    }

    static byte[] CreateSplitWalBytes(byte[] key) => Frame(
        WalCodec.EncodeTransactionMarker(
            WalOperation.TransactionBegin,
            1,
            1,
            7),
        WalCodec.EncodeTransactionMutation(
            new WalMutation(
                0,
                WalOperation.Put,
                key,
                "value"u8.ToArray(),
                2,
                null,
                null),
            1,
            7),
        WalCodec.EncodeTransactionMarker(
            WalOperation.TransactionCommit,
            1,
            3,
            7));

    static byte[] CreateMalformedMutationPayload(
        WalOperation operation,
        ulong? transactionId) =>
        WalCodec.EncodeRecord(new WalRecord(
            0,
            operation,
            "middle"u8.ToArray(),
            null,
            transactionId.HasValue ? 2UL : 3UL,
            null,
            null,
            transactionId,
            7));

    static byte[] Frame(params byte[][] payloads)
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

    static ManifestState CreateManifest(byte[] smallestKey, byte[] largestKey) => new()
    {
        LastPersistedSequence = 3,
        Files =
        [
            new FileMeta
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
