using System.Buffers.Binary;
using System.IO.Hashing;

namespace Cntryl.Pants.Storage.Internal.Wal;

static class WalSegmentCodec
{
    public const int HeaderSize = 128;

    public static byte[] EncodeHeader(WalOpenFacts facts, Guid holder)
    {
        var bytes = new byte[HeaderSize];
        "MWSEGHDR"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(12), facts.Id);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(20), facts.Epoch);
        holder.TryWriteBytes(bytes.AsSpan(28, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(44), facts.FirstSeq);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(52), facts.Prev.Id);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(60), facts.Prev.LastSeq);
        facts.Prev.Digest.CopyTo(bytes, 68);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(124), DiskFormat.Crc32C(bytes.AsSpan(0, 124)));
        return bytes;
    }

    public static (WalOpenFacts Open, Guid Holder) DecodeHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes[..8].SequenceEqual("MWSEGHDR"u8))
        {
            throw new PantsCorruptionException("WAL segment header is missing or truncated.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != 2)
        {
            throw new PantsCompatibilityException("Unsupported WAL segment header version.");
        }

        if (bytes.Slice(84, 40).IndexOfAnyExcept((byte)0) >= 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[124..]) != DiskFormat.Crc32C(bytes[..124]))
        {
            throw new PantsCorruptionException("WAL segment header checksum or reserved bytes are invalid.");
        }

        var open = new WalOpenFacts(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[20..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[44..]),
            new WalChainLink(BinaryPrimitives.ReadUInt64LittleEndian(bytes[52..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[60..]), bytes.Slice(68, 16).ToArray()));
        var holder = new Guid(bytes.Slice(28, 16), bigEndian: true);
        if (open.Id <= open.Prev.Id || open.FirstSeq <= open.Prev.LastSeq)
        {
            throw new PantsCorruptionException("WAL segment header chain is invalid.");
        }

        return (open, holder);
    }

    public static bool IsFooter(ReadOnlySpan<byte> payload) =>
        payload.Length >= 3 && payload[..2].SequenceEqual("MF"u8);

    public static byte[] Digest(ReadOnlySpan<byte> bytes)
    {
        var digest = new byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(digest, XxHash128.HashToUInt128(bytes));
        return digest;
    }

    public static byte[] Digest(Stream stream, long offset, long length, CancellationToken cancellationToken = default)
    {
        var position = stream.Position;
        try
        {
            stream.Position = offset;
            var hash = new XxHash128();
            var buffer = new byte[64 * 1024];
            while (length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(buffer.Length, length);
                if (!DiskFormat.ReadExactly(stream, buffer.AsSpan(0, requested)))
                {
                    throw new PantsCorruptionException("WAL segment changed while computing its digest.");
                }

                hash.Append(buffer.AsSpan(0, requested));
                length -= requested;
            }

            var digest = new byte[16];
            BinaryPrimitives.WriteUInt128LittleEndian(digest, hash.GetCurrentHashAsUInt128());
            return digest;
        }
        finally
        {
            stream.Position = position;
        }
    }

    public static WalSealedFacts Seal(FileStream stream)
    {
        stream.Flush();
        stream.Position = 0;
        var header = new byte[HeaderSize];
        if (!DiskFormat.ReadExactly(stream, header))
        {
            throw new PantsCorruptionException("WAL segment header is truncated.");
        }

        var (open, _) = DecodeHeader(header);
        stream.Position = 0;
        var scan = WalFrameReader.ScanSegment(stream);
        if (scan.Footer is { } existing)
        {
            stream.Flush(true);
            return existing;
        }

        var summary = scan.Summary;
        var length = stream.Length;
        var facts = new WalSealedFacts(new WalChainLink(open.Id, summary.LastSequence, Digest(stream, 0, length)),
            summary.RecordCount, checked((ulong)length),
            summary.MaximumSequenceByFamily.Select(static pair => new[] { (ulong)pair.Key, pair.Value }).ToArray());
        using var payload = new MemoryStream();
        payload.Write("MF"u8);
        payload.WriteByte(1);
        DiskFormat.WriteUInt64(payload, open.Id);
        DiskFormat.WriteUInt64(payload, summary.RecordCount);
        DiskFormat.WriteUInt64(payload, summary.FirstSequence);
        DiskFormat.WriteUInt64(payload, summary.LastSequence);
        DiskFormat.WriteUInt64(payload, checked((ulong)length));
        DiskFormat.WriteUInt32(payload, checked((uint)summary.MaximumSequenceByFamily.Count));
        foreach (var pair in summary.MaximumSequenceByFamily)
        {
            DiskFormat.WriteUInt32(payload, pair.Key);
            DiskFormat.WriteUInt64(payload, pair.Value);
        }

        payload.Write(facts.Link.Digest);
        WalCodec.AppendFrame(stream.SafeFileHandle, length, payload.ToArray());
        stream.Flush(true);
        stream.Seek(0, SeekOrigin.End);
        return facts;
    }

    public static WalSealedFacts ValidateFooter(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> preceding,
        WalOpenFacts open, IReadOnlyList<WalRecord> records)
    {
        var summary = new WalSegmentSummary(open);
        foreach (var record in records)
        {
            summary.Observe(record);
        }

        return ValidateFooter(payload, Digest(preceding), checked((ulong)preceding.Length), open, summary);
    }

    public static WalSealedFacts ValidateFooter(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> digest,
        ulong dataLength, WalOpenFacts open, WalSegmentSummary summary)
    {
        if (payload.Length < 63 || !IsFooter(payload) || payload[2] != 1)
        {
            throw new PantsCorruptionException("WAL segment footer is malformed.");
        }

        var count = BinaryPrimitives.ReadUInt64LittleEndian(payload[11..]);
        var first = BinaryPrimitives.ReadUInt64LittleEndian(payload[19..]);
        var last = BinaryPrimitives.ReadUInt64LittleEndian(payload[27..]);
        var length = BinaryPrimitives.ReadUInt64LittleEndian(payload[35..]);
        var familyCount = BinaryPrimitives.ReadUInt32LittleEndian(payload[43..]);
        if (familyCount > (payload.Length - 63) / 12 || payload.Length != 63L + familyCount * 12L ||
            BinaryPrimitives.ReadUInt64LittleEndian(payload[3..]) != open.Id ||
            count != summary.RecordCount || length != dataLength ||
            first != summary.FirstSequence || last != summary.LastSequence ||
            !payload[^16..].SequenceEqual(digest))
        {
            throw new PantsCorruptionException("WAL segment footer disagrees with its records or digest.");
        }

        var expected = summary.MaximumSequenceByFamily
            .Select(static pair => new[] { (ulong)pair.Key, pair.Value }).ToArray();
        if (expected.Length != familyCount)
        {
            throw new PantsCorruptionException("WAL footer family coverage is invalid.");
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(payload[(47 + index * 12)..]) != expected[index][0] ||
                BinaryPrimitives.ReadUInt64LittleEndian(payload[(51 + index * 12)..]) != expected[index][1])
            {
                throw new PantsCorruptionException("WAL footer family maxima are invalid.");
            }
        }

        return new WalSealedFacts(new WalChainLink(open.Id, last, payload[^16..].ToArray()), count, length, expected);
    }
}
