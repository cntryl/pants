using System.Buffers.Binary;

namespace Cntryl.Pants.Support.TestDoubles;

static class TestWalSegment
{
    public static SealedWalSegment Create(ulong id, ulong epoch, ulong maximumSequence, string fileName, byte[] value, WalChainLink? previousLink = null)
    {
        var previous = previousLink ?? (id <= 1 ? WalChainLink.Genesis : Create(id - 1, epoch, maximumSequence - 1, "previous.wal", [unchecked((byte)(id - 1))]).Facts.Link);
        var payload = WalCodec.EncodeRecord(new WalRecord(0, WalOperation.Put, "key"u8.ToArray(), value, maximumSequence, null, null, null, epoch));
        using var records = new MemoryStream();
        WriteFrame(records, payload);
        return SealRecords(id, epoch, fileName, records.ToArray(), previous);
    }

    public static SealedWalSegment SealRecords(ulong id, ulong epoch, string fileName, byte[] recordFrames, WalChainLink previous)
    {
        var records = new List<WalRecord>();
        WalFrameReader.Visit(recordFrames, (record, _) => records.Add(record));
        var maximumSequence = records[^1].Sequence;
        var families = records.GroupBy(static record => record.ColumnFamilyId).OrderBy(static group => group.Key).ToArray();
        var header = WalSegmentCodec.EncodeHeader(new WalOpenFacts(id, epoch, WalCodec.GetSequenceStart(records[0]), previous), new Guid("11111111-1111-1111-1111-111111111111"));
        using var stream = new MemoryStream();
        stream.Write(header);
        stream.Write(recordFrames);
        var data = stream.ToArray();
        using var footer = new MemoryStream();
        footer.Write("MF"u8);
        footer.WriteByte(1);
        DiskFormat.WriteUInt64(footer, id);
        DiskFormat.WriteUInt64(footer, checked((ulong)records.Count));
        DiskFormat.WriteUInt64(footer, records[0].Sequence);
        DiskFormat.WriteUInt64(footer, maximumSequence);
        DiskFormat.WriteUInt64(footer, checked((ulong)data.Length));
        DiskFormat.WriteUInt32(footer, checked((uint)families.Length));
        foreach (var family in families)
        {
            DiskFormat.WriteUInt32(footer, family.Key);
            DiskFormat.WriteUInt64(footer, family.Max(static record => record.Sequence));
        }

        footer.Write(WalSegmentCodec.Digest(data));
        WriteFrame(stream, footer.ToArray());
        return new SealedWalSegment(id, epoch, maximumSequence, fileName, stream.ToArray());
    }

    public static ProviderPublishedWalSegment Publish(SealedWalSegment segment) => new()
    {
        SegmentId = segment.SegmentId,
        WriterEpoch = segment.WriterEpoch,
        MaximumSequence = segment.MaximumSequence,
        SizeBytes = checked((ulong)segment.Bytes.Length),
        ContentCrc32C = DiskFormat.Crc32C(segment.Bytes),
        ObjectKey = PantsCloudObjectLayout.WalSegmentObjectKey(segment.WriterEpoch, segment.SegmentId),
        Previous = segment.Header.Prev,
        Facts = segment.Facts
    };

    static void WriteFrame(Stream stream, byte[] payload)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], DiskFormat.Crc32C(payload));
        stream.Write(header);
        stream.Write(payload);
    }
}
