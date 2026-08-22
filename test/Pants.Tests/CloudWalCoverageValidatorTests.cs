using System.Buffers.Binary;

namespace Pants.Tests;

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
        var bytes = new byte[2 * sizeof(uint) + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(sizeof(uint)),
            MidgeDiskFormat.Crc32C(payload));
        payload.CopyTo(bytes.AsSpan(2 * sizeof(uint)));
        return bytes;
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
