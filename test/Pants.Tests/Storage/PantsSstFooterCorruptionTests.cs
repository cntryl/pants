using System.Buffers.Binary;

namespace Cntryl.Pants.Tests.Storage;

public sealed class PantsSstFooterCorruptionTests
{
    [Fact]
    public void ShouldRejectFooterAsCorruptGivenCrcMismatchWithNoLegacyMagicMatch()
    {
        var footer = BuildValidFooter();

        // Flip a bit inside the magic field, which also breaks the CRC that
        // covers it, and does not happen to land on the legacy magic value.
        footer[72] ^= 0xFF;

        var exception = Assert.Throws<PantsCorruptionException>(
            () => SstCodec.ValidateFooter(footer));
        Assert.DoesNotContain("compat", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldRejectFooterAsIncompatibleGivenUnsupportedFormatVersion()
    {
        var footer = BuildValidFooter();
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(64), 999);
        RecomputeCrc(footer);

        Assert.Throws<PantsCompatibilityException>(
            () => SstCodec.ValidateFooter(footer));
    }

    [Fact]
    public void ShouldReportCompatibilityErrorGivenFileTooShortButTrailingBytesMatchLegacyMagic()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "short-legacy.sst");
        var bytes = new byte[40];
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(bytes.Length - sizeof(ulong)),
            DiskFormat.SstFooterMagic);
        File.WriteAllBytes(path, bytes);

        Assert.Throws<PantsCompatibilityException>(() => SstReader.Open(path));
    }

    [Fact]
    public void ShouldReportCorruptionGivenFileTooShortAndNoLegacyMagicMatch()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "short-garbage.sst");
        var bytes = new byte[40];
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(bytes.Length - sizeof(ulong)),
            0xDEAD_BEEF_0000_0001);
        File.WriteAllBytes(path, bytes);

        Assert.Throws<PantsCorruptionException>(() => SstReader.Open(path));
    }

    static byte[] BuildValidFooter()
    {
        var entries = new[]
        {
            new SstEntry(TestBytes.FromString("key"), TestBytes.FromString("value"), 1, null, false)
        };
        var bytes = SstCodec.Encode(entries, [], PantsPerformanceGoal.Latency);
        return bytes.AsSpan(bytes.Length - DiskFormat.SstFooterSize).ToArray();
    }

    static void RecomputeCrc(byte[] footer) =>
        BinaryPrimitives.WriteUInt32LittleEndian(
            footer.AsSpan(80),
            DiskFormat.Crc32C(footer.AsSpan(0, 80)));
}
