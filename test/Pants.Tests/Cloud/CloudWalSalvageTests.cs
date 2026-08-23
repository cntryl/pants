namespace Cntryl.Pants.Tests.Cloud;

public sealed class CloudWalSalvageTests
{
    [Fact]
    public void ShouldReturnSalvageStopGivenValidSplitWalFrames()
    {
        var bytes = CreateSplitWalBytes();

        var recoveryBytes = CloudWalSalvage.CreateLocalRecoveryBytes(bytes);

        Assert.Equal(new byte[1], recoveryBytes.ToArray());
    }

    [Fact]
    public void ShouldRetainRemoteBytesGivenTornSplitWalCommitFrame()
    {
        var validBytes = CreateSplitWalBytes();
        var tornBytes = validBytes[..^1];

        var recoveryBytes = CloudWalSalvage.CreateLocalRecoveryBytes(tornBytes);

        Assert.Equal(tornBytes, recoveryBytes.ToArray());
    }

    static byte[] CreateSplitWalBytes() => Frame(
        WalCodec.EncodeTransactionMarker(
            WalOperation.TransactionBegin,
            1,
            1,
            7),
        WalCodec.EncodeTransactionMutation(
            new WalMutation(
                0,
                WalOperation.Put,
                "alpha"u8.ToArray(),
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
}
