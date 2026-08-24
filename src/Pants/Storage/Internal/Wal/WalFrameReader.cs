using System.Buffers.Binary;

namespace Cntryl.Pants.Storage.Internal.Wal;

static class WalFrameReader
{
    public static void Visit(
        Stream stream,
        Action<WalRecord, int> visitor,
        CancellationToken cancellationToken = default) =>
        Visit(stream, visitor, WalTailPolicy.Strict, cancellationToken);

    public static void Visit(
        Stream stream,
        Action<WalRecord, int> visitor,
        WalTailPolicy tailPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(visitor);
        Span<byte> header = stackalloc byte[2 * sizeof(uint)];
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frameStart = stream.Position;
            if (stream.Length - frameStart < header.Length)
            {
                if (tailPolicy == WalTailPolicy.AllowIncompleteFinalTail)
                {
                    return;
                }

                throw new StorageException("The WAL has a torn frame header.");
            }

            if (!DiskFormat.ReadExactly(stream, header))
            {
                throw new StorageException("The WAL has a torn frame header.");
            }

            if (header.IndexOfAnyExcept((byte)0) < 0)
            {
                var isZeroFilledTail = IsZeroFilledTail(stream);
                if (isZeroFilledTail)
                {
                    if (tailPolicy == WalTailPolicy.AllowIncompleteFinalTail)
                    {
                        return;
                    }

                    throw new StorageException("The WAL has a zero-filled final tail.");
                }
            }

            var encodedLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (encodedLength > DiskFormat.WalMaximumRecordBytes)
            {
                throw new StorageException("A WAL frame exceeds the 64 MiB limit.");
            }

            if (encodedLength > stream.Length - stream.Position)
            {
                if (tailPolicy == WalTailPolicy.AllowIncompleteFinalTail &&
                    !ContainsVerifiedFrameInRemainingBytes(stream, cancellationToken))
                {
                    return;
                }

                throw new StorageException(
                    tailPolicy == WalTailPolicy.AllowIncompleteFinalTail
                        ? "A WAL frame length hides a verified later frame."
                        : "The WAL has a torn frame payload.");
            }

            var payload = GC.AllocateUninitializedArray<byte>(checked((int)encodedLength));
            if (!DiskFormat.ReadExactly(stream, payload))
            {
                throw new StorageException("The WAL has a torn frame payload.");
            }

            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[sizeof(uint)..]);
            if (DiskFormat.Crc32C(payload) != expectedCrc)
            {
                throw new StorageException("A WAL frame checksum does not match.");
            }

            visitor(WalCodec.DecodeRecord(payload), payload.Length);
        }
    }

    internal static bool IsZeroFilledTail(Stream stream)
    {
        var start = stream.Position;
        Span<byte> buffer = stackalloc byte[4096];
        while (stream.Position < stream.Length)
        {
            var requested = checked((int)Math.Min(buffer.Length, stream.Length - stream.Position));
            var read = stream.Read(buffer[..requested]);
            if (read == 0 || buffer[..read].IndexOfAnyExcept((byte)0) >= 0)
            {
                stream.Position = start;
                return false;
            }
        }

        return true;
    }

    public static bool ContainsVerifiedFrameInRemainingBytes(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var remaining = checked((int)(stream.Length - stream.Position));
        var bytes = GC.AllocateUninitializedArray<byte>(remaining);
        if (!DiskFormat.ReadExactly(stream, bytes))
        {
            throw new StorageException("The WAL changed while its final tail was inspected.");
        }

        const int headerLength = 2 * sizeof(uint);
        for (var payloadStart = headerLength;
             payloadStart <= bytes.Length - 3;
             payloadStart++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!WalCodec.HasCurrentRecordPrefix(bytes.AsSpan(payloadStart)))
            {
                continue;
            }

            var header = bytes.AsSpan(payloadStart - headerLength, headerLength);
            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (payloadLength > DiskFormat.WalMaximumRecordBytes ||
                payloadLength > bytes.Length - payloadStart)
            {
                continue;
            }

            var payload = bytes.AsSpan(payloadStart, checked((int)payloadLength));
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[sizeof(uint)..]);
            if (DiskFormat.Crc32C(payload) != expectedCrc)
            {
                continue;
            }

            try
            {
                _ = WalCodec.DecodeRecord(payload);
                return true;
            }
            catch (PantsException)
            {
            }
        }

        return false;
    }

    public static void Visit(
        ReadOnlySpan<byte> bytes,
        Action<WalRecord, int> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        var cursor = 0;
        while (cursor < bytes.Length)
        {
            if (bytes.Length - cursor < 2 * sizeof(uint))
            {
                throw new StorageException("The WAL has a torn frame header.");
            }

            var encodedLength = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.Slice(cursor, sizeof(uint)));
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.Slice(cursor + sizeof(uint), sizeof(uint)));
            cursor += 2 * sizeof(uint);
            if (encodedLength > DiskFormat.WalMaximumRecordBytes ||
                encodedLength > bytes.Length - cursor)
            {
                throw new StorageException("The WAL has a torn or oversized frame payload.");
            }

            var payloadLength = checked((int)encodedLength);
            var payload = bytes.Slice(cursor, payloadLength);
            if (DiskFormat.Crc32C(payload) != expectedCrc)
            {
                throw new StorageException("A WAL frame checksum does not match.");
            }

            visitor(WalCodec.DecodeRecord(payload), payloadLength);
            cursor += payloadLength;
        }
    }
}
