using System.Text;

namespace Cntryl.Pants.Storage.Compression;

public sealed class ZstdCodecTests
{
    // Frames produced by reference libzstd (the engine behind Midge's zstd crate) for ExpectedPayload().
    const string ReferenceLevel3Frame =
        "28b52ffd6080015d0200140270616e74732d6d696467652d3030303b3132333435363738393130313233343536120060134b05201fe818e80474073a063a013a0432039d80ee40c74027a03bd031d009e8ae91e712";
    const string ReferenceLevel9Frame =
        "28b52ffd608001f50100040270616e74732d6d696467652d3030303b3132333435363738393130313233343512a830f6fa67b0e36a0310feca5effff3b5f139930fa9fc2a030c5a6";

    static byte[] ExpectedPayload()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 40; index++)
        {
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"pants-midge-{index % 17:000};");
        }

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    [Theory]
    [InlineData(ReferenceLevel3Frame, (byte)2)]
    [InlineData(ReferenceLevel9Frame, (byte)3)]
    public void ShouldDecodeReferenceZstdFramesAtBothLevels(string frameHex, byte algorithm)
    {
        var decoded = DiskFormat.Decompress(Convert.FromHexString(frameHex), algorithm);

        Assert.Equal(ExpectedPayload(), decoded);
    }

    [Theory]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    public void ShouldRoundTripCompressedSstBlocksWithFrameContentSize(byte code)
    {
        var algorithm = SstBlockCodec.ParseAlgorithm(code);
        var payload = Enumerable.Range(0, 20_000).Select(i => (byte)(i % 251 / 3)).ToArray();

        var block = SstBlockCodec.CompressWithTrailer(payload, algorithm);

        Assert.Equal((byte)algorithm, block[^5]);
        Assert.True(block.Length < payload.Length);
        Assert.Equal(payload.Length, SstBlockCodec.GetDecodedSize(block));
        Assert.Equal(payload, SstBlockCodec.DecompressWithTrailer(block));
    }

    [Fact]
    public void ShouldRejectZstdFrameThatExceedsExplicitLimit()
    {
        var frame = Convert.FromHexString(ReferenceLevel3Frame);

        Assert.ThrowsAny<Exception>(() => DiskFormat.Decompress(frame, 2, 100));
    }

    [Fact]
    public void ShouldRejectTruncatedZstdFrame()
    {
        var frame = Convert.FromHexString(ReferenceLevel9Frame);

        Assert.ThrowsAny<Exception>(() => DiskFormat.Decompress(frame.AsSpan(0, frame.Length - 8), 3));
    }
}
