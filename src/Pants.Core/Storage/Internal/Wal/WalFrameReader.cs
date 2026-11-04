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
        => _ = VisitCore(stream, visitor, tailPolicy, cancellationToken);

    public static void VisitRecovery(
        Stream stream, Action<WalRecord, int> visitor, RecoveryWorkSink? recoveryWork,
        CancellationToken cancellationToken = default) =>
        VisitRecovery(stream, visitor, recoveryWork, null, cancellationToken);

    public static void VisitRecovery(
        Stream stream, Action<WalRecord, int> visitor, RecoveryWorkSink? recoveryWork,
        long? maximumReplayableTransactionBytes, CancellationToken cancellationToken = default) =>
        _ = VisitCore(stream, visitor, WalTailPolicy.Strict, cancellationToken, recoveryWork: recoveryWork,
            maximumReplayableTransactionBytes: maximumReplayableTransactionBytes);

    public static (WalSegmentSummary Summary, WalSealedFacts? Footer) ScanSegment(
        Stream stream, CancellationToken cancellationToken = default) => ScanSegment(stream, null, cancellationToken);

    public static (WalSegmentSummary Summary, WalSealedFacts? Footer) ScanSegment(
        Stream stream, long? maximumReplayableTransactionBytes, CancellationToken cancellationToken = default) =>
        VisitCore(stream, static (_, _) => { }, WalTailPolicy.Strict, cancellationToken,
            maximumReplayableTransactionBytes: maximumReplayableTransactionBytes);

    public static void ValidateDurablePrefix(Stream stream, long endPosition,
        CancellationToken cancellationToken = default) => ValidateDurablePrefix(stream, endPosition, null, cancellationToken);

    public static void ValidateDurablePrefix(Stream stream, long endPosition,
        long? maximumReplayableTransactionBytes, CancellationToken cancellationToken = default) =>
        _ = VisitCore(stream, static (_, _) => { }, WalTailPolicy.Strict, cancellationToken, endPosition,
            maximumReplayableTransactionBytes: maximumReplayableTransactionBytes);

    static (WalSegmentSummary Summary, WalSealedFacts? Footer) VisitCore(
        Stream stream, Action<WalRecord, int> visitor, WalTailPolicy tailPolicy,
        CancellationToken cancellationToken, long? endPosition = null, RecoveryWorkSink? recoveryWork = null,
        long? maximumReplayableTransactionBytes = null)
    {
        var progress = recoveryWork?.Begin("sealed_inspection");
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(visitor);
        var segmentEnd = endPosition ?? stream.Length;
        if (segmentEnd > stream.Length || segmentEnd < stream.Position)
        {
            throw new PantsCorruptionException("WAL durable prefix lies outside its segment.");
        }

        var segmentStart = stream.Position;
        WalOpenFacts? open = null;
        if (segmentEnd - stream.Position >= WalSegmentCodec.HeaderSize)
        {
            Span<byte> segmentHeader = stackalloc byte[WalSegmentCodec.HeaderSize];
            DiskFormat.ReadExactly(stream, segmentHeader);
            if (segmentHeader[..8].SequenceEqual("MWSEGHDR"u8))
            {
                (open, _) = WalSegmentCodec.DecodeHeader(segmentHeader);
            }
            else
            {
                stream.Position = segmentStart;
            }
        }

        var summary = new WalSegmentSummary(open);
        Span<byte> header = stackalloc byte[2 * sizeof(uint)];
        while (stream.Position < segmentEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frameStart = stream.Position;
            if (segmentEnd - frameStart < header.Length)
            {
                if (tailPolicy == WalTailPolicy.AllowIncompleteFinalTail)
                {
                    return (summary, null);
                }

                throw new StorageException("The WAL has a torn frame header.");
            }

            if (!DiskFormat.ReadExactly(stream, header))
            {
                throw new StorageException("The WAL has a torn frame header.");
            }

            if (header.IndexOfAnyExcept((byte)0) < 0)
            {
                var isZeroFilledTail = IsZeroFilledTail(stream, cancellationToken);
                if (isZeroFilledTail)
                {
                    if (tailPolicy == WalTailPolicy.AllowIncompleteFinalTail)
                    {
                        return (summary, null);
                    }

                    throw new StorageException("The WAL has a zero-filled final tail.");
                }
            }

            var encodedLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (encodedLength > DiskFormat.WalMaximumRecordBytes)
            {
                throw new StorageException("A WAL frame exceeds the 64 MiB limit.");
            }

            if (encodedLength > segmentEnd - stream.Position)
            {
                if (tailPolicy == WalTailPolicy.AllowIncompleteFinalTail &&
                    !ContainsVerifiedFrameInRemainingBytes(stream, maximumReplayableTransactionBytes, cancellationToken))
                {
                    return (summary, null);
                }

                throw new StorageException(
                    tailPolicy == WalTailPolicy.AllowIncompleteFinalTail
                        ? "A WAL frame length hides a verified later frame."
                        : "The WAL has a torn frame payload.");
            }

            WalCodec.RequireReplayFrame(encodedLength, maximumReplayableTransactionBytes);
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

            if (WalSegmentCodec.IsFooter(payload))
            {
                if (open is null || stream.Position != segmentEnd)
                {
                    throw new PantsCorruptionException("WAL footer has no segment header or is followed by bytes.");
                }

                var length = frameStart - segmentStart;
                var digest = WalSegmentCodec.Digest(stream, segmentStart, length, cancellationToken);
                var facts = WalSegmentCodec.ValidateFooter(payload, digest, checked((ulong)length), open, summary);
                progress?.Finish();
                return (summary, facts);
            }

            var record = WalCodec.DecodeRecord(payload, open?.Epoch ?? 0, maximumReplayableTransactionBytes);
            summary.Observe(record);
            visitor(record, payload.Length);
            progress?.Completed(stream.Position - frameStart, 1);
        }

        progress?.Finish();
        return (summary, null);
    }

    internal static bool IsZeroFilledTail(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var start = stream.Position;
        Span<byte> buffer = stackalloc byte[4096];
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        Stream stream, CancellationToken cancellationToken = default) =>
        ContainsVerifiedFrameInRemainingBytes(stream, null, cancellationToken);

    public static bool ContainsVerifiedFrameInRemainingBytes(
        Stream stream, long? maximumReplayableTransactionBytes, CancellationToken cancellationToken = default)
    {
        const int headerLength = 2 * sizeof(uint);
        const int overlapLength = headerLength + 2;
        var buffer = new byte[64 * 1024];
        var carry = 0;
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkOffset = stream.Position - carry;
            var read = stream.Read(buffer.AsSpan(carry));
            if (read == 0)
            {
                throw new StorageException("The WAL changed while its final tail was inspected.");
            }

            var used = carry + read;
            for (var payloadStart = headerLength; payloadStart <= used - 3; payloadStart++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!WalCodec.HasCurrentRecordPrefix(buffer.AsSpan(payloadStart, used - payloadStart)))
                {
                    continue;
                }

                var header = buffer.AsSpan(payloadStart - headerLength, headerLength);
                var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
                var absolutePayloadStart = chunkOffset + payloadStart;
                if (payloadLength > DiskFormat.WalMaximumRecordBytes ||
                    payloadLength > stream.Length - absolutePayloadStart)
                {
                    continue;
                }

                WalCodec.RequireReplayFrame(payloadLength, maximumReplayableTransactionBytes);
                var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[sizeof(uint)..]);
                if (payloadLength <= used - payloadStart)
                {
                    if (IsVerifiedRecord(buffer.AsSpan(payloadStart, (int)payloadLength), expectedCrc, maximumReplayableTransactionBytes))
                    {
                        return true;
                    }
                }
                else
                {
                    var position = stream.Position;
                    try
                    {
                        stream.Position = absolutePayloadStart;
                        var payload = GC.AllocateUninitializedArray<byte>((int)payloadLength);
                        if (!DiskFormat.ReadExactly(stream, payload))
                        {
                            throw new StorageException("The WAL changed while its final tail was inspected.");
                        }

                        if (IsVerifiedRecord(payload, expectedCrc, maximumReplayableTransactionBytes))
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        stream.Position = position;
                    }
                }
            }

            carry = Math.Min(overlapLength, used);
            buffer.AsSpan(used - carry, carry).CopyTo(buffer);
        }

        return false;
    }

    static bool IsVerifiedRecord(ReadOnlySpan<byte> payload, uint expectedCrc, long? maximumReplayableTransactionBytes)
    {
        if (DiskFormat.Crc32C(payload) != expectedCrc)
        {
            return false;
        }

        try
        {
            _ = WalCodec.DecodeRecord(payload, maximumDecodedBytes: maximumReplayableTransactionBytes);
            return true;
        }
        catch (PantsException failure) when (failure is not PantsResourceLimitException)
        {
            return false;
        }
    }

    public static void Visit(
        ReadOnlySpan<byte> bytes,
        Action<WalRecord, int> visitor,
        CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        Visit(stream, visitor, cancellationToken);
    }

    public static void Visit(ReadOnlySpan<byte> bytes, Action<WalRecord, int> visitor,
        WalTailPolicy tailPolicy, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        Visit(stream, visitor, tailPolicy, cancellationToken);
    }
}
