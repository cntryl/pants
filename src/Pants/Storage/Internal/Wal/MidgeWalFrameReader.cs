using System.Buffers.Binary;

namespace Cntryl.Pants;

internal static class MidgeWalFrameReader
{
    public static void Visit(
        Stream stream,
        Action<MidgeWalRecord, int> visitor,
        CancellationToken cancellationToken = default) =>
        Visit(stream, visitor, MidgeWalTailPolicy.Strict, cancellationToken);

    public static void Visit(
        Stream stream,
        Action<MidgeWalRecord, int> visitor,
        MidgeWalTailPolicy tailPolicy,
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
                if (tailPolicy == MidgeWalTailPolicy.AllowIncompleteFinalTail)
                {
                    return;
                }

                throw new PantsStorageException("The WAL has a torn frame header.");
            }

            if (!MidgeDiskFormat.ReadExactly(stream, header))
            {
                throw new PantsStorageException("The WAL has a torn frame header.");
            }

            var payloadStart = stream.Position;
            if (header.IndexOfAnyExcept((byte)0) < 0)
            {
                var isZeroFilledTail = IsZeroFilledTail(stream);
                if (isZeroFilledTail)
                {
                    if (tailPolicy == MidgeWalTailPolicy.AllowIncompleteFinalTail)
                    {
                        return;
                    }

                    throw new PantsStorageException("The WAL has a zero-filled final tail.");
                }

                stream.Position = payloadStart;
            }

            var encodedLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (encodedLength > MidgeDiskFormat.WalMaximumRecordBytes)
            {
                throw new PantsStorageException("A WAL frame exceeds the 64 MiB limit.");
            }

            if (encodedLength > stream.Length - stream.Position)
            {
                if (tailPolicy == MidgeWalTailPolicy.AllowIncompleteFinalTail &&
                    !ContainsVerifiedFrameInRemainingBytes(stream, cancellationToken))
                {
                    return;
                }

                throw new PantsStorageException(
                    tailPolicy == MidgeWalTailPolicy.AllowIncompleteFinalTail
                        ? "A WAL frame length hides a verified later frame."
                        : "The WAL has a torn frame payload.");
            }

            var payload = GC.AllocateUninitializedArray<byte>(checked((int)encodedLength));
            if (!MidgeDiskFormat.ReadExactly(stream, payload))
            {
                throw new PantsStorageException("The WAL has a torn frame payload.");
            }

            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[sizeof(uint)..]);
            if (MidgeDiskFormat.Crc32C(payload) != expectedCrc)
            {
                throw new PantsStorageException("A WAL frame checksum does not match.");
            }

            visitor(MidgeWalCodec.DecodeRecord(payload), payload.Length);
        }
    }

    static bool IsZeroFilledTail(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4096];
        while (stream.Position < stream.Length)
        {
            var requested = checked((int)Math.Min(buffer.Length, stream.Length - stream.Position));
            var read = stream.Read(buffer[..requested]);
            if (read == 0 || buffer[..read].IndexOfAnyExcept((byte)0) >= 0)
            {
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
        if (!MidgeDiskFormat.ReadExactly(stream, bytes))
        {
            throw new PantsStorageException("The WAL changed while its final tail was inspected.");
        }

        const int headerLength = 2 * sizeof(uint);
        for (var payloadStart = headerLength;
             payloadStart <= bytes.Length - 3;
             payloadStart++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MidgeWalCodec.HasCurrentRecordPrefix(bytes.AsSpan(payloadStart)))
            {
                continue;
            }

            var header = bytes.AsSpan(payloadStart - headerLength, headerLength);
            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (payloadLength > MidgeDiskFormat.WalMaximumRecordBytes ||
                payloadLength > bytes.Length - payloadStart)
            {
                continue;
            }

            var payload = bytes.AsSpan(payloadStart, checked((int)payloadLength));
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[sizeof(uint)..]);
            if (MidgeDiskFormat.Crc32C(payload) != expectedCrc)
            {
                continue;
            }

            try
            {
                _ = MidgeWalCodec.DecodeRecord(payload);
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
        Action<MidgeWalRecord, int> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        var cursor = 0;
        while (cursor < bytes.Length)
        {
            if (bytes.Length - cursor < 2 * sizeof(uint))
            {
                throw new PantsStorageException("The WAL has a torn frame header.");
            }

            var encodedLength = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.Slice(cursor, sizeof(uint)));
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.Slice(cursor + sizeof(uint), sizeof(uint)));
            cursor += 2 * sizeof(uint);
            if (encodedLength > MidgeDiskFormat.WalMaximumRecordBytes ||
                encodedLength > bytes.Length - cursor)
            {
                throw new PantsStorageException("The WAL has a torn or oversized frame payload.");
            }

            var payloadLength = checked((int)encodedLength);
            var payload = bytes.Slice(cursor, payloadLength);
            if (MidgeDiskFormat.Crc32C(payload) != expectedCrc)
            {
                throw new PantsStorageException("A WAL frame checksum does not match.");
            }

            visitor(MidgeWalCodec.DecodeRecord(payload), payloadLength);
            cursor += payloadLength;
        }
    }
}
