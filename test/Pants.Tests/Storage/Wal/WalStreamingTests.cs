using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class WalStreamingTests
{
    [Fact]
    public void ShouldReleaseDecodedValuesBeforeSegmentScanCompletes()
    {
        using var directory = new TemporaryDirectory();
        var ledger = WalLedgerStore.LoadOrCreate(directory.Path, 1);
        using var stream = new FileStream(ledger.ActivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        for (var sequence = 1UL; sequence <= 128; sequence++)
        {
            var record = new WalRecord(0, WalOperation.Put, "key"u8.ToArray(), new byte[1024],
                sequence, null, null, null, 1);
            WalCodec.AppendFrame(stream.SafeFileHandle, stream.Length, WalCodec.EncodeRecord(record));
        }

        _ = WalSegmentCodec.Seal(stream);
        stream.Position = 0;
        WeakReference<byte[]>? firstValue = null;
        var released = false;

        WalFrameReader.Visit(stream, (record, _) =>
        {
            if (record.Sequence == 1)
            {
                firstValue = new WeakReference<byte[]>(record.Value!);
            }
            else if (record.Sequence == 128)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                released = !firstValue!.TryGetTarget(out var retainedValue);
            }
        });

        Assert.True(released, "Scanning a segment must not retain earlier records' decoded values.");
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(-8)]
    [InlineData(-1)]
    [InlineData(1)]
    public void ShouldFindVerifiedLaterFrameAcrossTailScanChunkBoundaries(int boundaryOffset)
    {
        var record = WalCodec.EncodeRecord(new WalRecord(0, WalOperation.Put,
            "key"u8.ToArray(), "value"u8.ToArray(), 1, null, null, null, 1));
        var frameOffset = 64 * 1024 + boundaryOffset;
        using var stream = new MemoryStream();
        stream.Write(Enumerable.Repeat((byte)0xfe, frameOffset).ToArray());
        DiskFormat.WriteUInt32(stream, (uint)record.Length);
        DiskFormat.WriteUInt32(stream, DiskFormat.Crc32C(record));
        stream.Write(record);
        stream.Position = 0;

        Assert.True(WalFrameReader.ContainsVerifiedFrameInRemainingBytes(stream));
    }

    [Fact]
    public void ShouldComputeIdenticalStreamingDigestForExactSegmentExtent()
    {
        var bytes = new byte[256 * 1024];
        new Random(7717).NextBytes(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        stream.Position = 13;

        var actual = WalSegmentCodec.Digest(stream, 7, bytes.Length - 20);

        Assert.Equal(WalSegmentCodec.Digest(bytes.AsSpan(7, bytes.Length - 20)), actual);
        Assert.Equal(13, stream.Position);
    }
}
