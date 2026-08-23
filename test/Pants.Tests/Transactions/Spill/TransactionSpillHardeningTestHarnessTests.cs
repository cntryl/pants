namespace Pants.Tests;

public sealed class TransactionSpillHardeningTestHarnessTests
{
    [Fact]
    public void ShouldRejectUnsupportedWalCompressionWhenInspectingFrame()
    {
        using var directory = new TemporaryDirectory();
        var walDirectory = Path.Combine(directory.Path, "wal");
        Directory.CreateDirectory(walDirectory);
        var payload = CreateWalPayloadWithUnsupportedCompression();
        using (var stream = File.Create(Path.Combine(walDirectory, "wal.log")))
        {
            MidgeDiskFormat.WriteUInt32(stream, checked((uint)payload.Length));
            MidgeDiskFormat.WriteUInt32(stream, MidgeDiskFormat.Crc32C(payload));
            stream.Write(payload);
        }

        Assert.Throws<PantsStorageException>(
            () => TransactionSpillHardeningTestHarness.ReadWalFrames(directory.Path));
    }

    static byte[] CreateWalPayloadWithUnsupportedCompression()
    {
        using var payload = new MemoryStream();
        payload.Write("MW"u8);
        payload.WriteByte(1);
        WriteTlv(payload, 1, [0]);
        WriteTlvUInt32(payload, 2, 0);
        WriteTlvUInt64(payload, 3, 1);
        WriteTlv(payload, 4, "key"u8);
        WriteTlv(payload, 5, "value"u8);
        WriteTlv(payload, 9, [byte.MaxValue]);
        WriteTlvUInt64(payload, 10, 1);
        return payload.ToArray();
    }

    static void WriteTlvUInt32(Stream stream, byte tag, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        WriteTlv(stream, tag, bytes);
    }

    static void WriteTlvUInt64(Stream stream, byte tag, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        WriteTlv(stream, tag, bytes);
    }

    static void WriteTlv(Stream stream, byte tag, ReadOnlySpan<byte> value)
    {
        stream.WriteByte(tag);
        MidgeDiskFormat.WriteUInt32(stream, checked((uint)value.Length));
        stream.Write(value);
    }
}
