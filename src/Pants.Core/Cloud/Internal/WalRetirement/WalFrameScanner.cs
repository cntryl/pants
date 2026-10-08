using System.Buffers.Binary;

namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

/// <summary>
///     Checks and acknowledges the complete WAL frames at the start of a fetched window. Frames are
///     strict: a published segment is sealed, so a torn or oversized frame is corruption.
/// </summary>
static class WalFrameScanner
{
    const int HeaderBytes = 2 * sizeof(uint);

    /// <summary>
    ///     Acknowledges frames from <paramref name="window" />, which starts at the proof's offset,
    ///     until the window ends, the segment ends, or a frame is not covered.
    /// </summary>
    /// <returns>
    ///     Zero when the proof advanced or stopped at an uncovered frame; otherwise the length of the
    ///     next frame, which the window was too small to hold.
    /// </returns>
    public static int Acknowledge(
        WalSegmentProof proof,
        ReadOnlySpan<byte> window,
        PublishedManifest manifest)
    {
        var cursor = 0;
        var acknowledged = false;
        while (proof.Remaining > 0)
        {
            var available = window.Length - cursor;
            if (proof.Remaining < HeaderBytes)
            {
                throw new PantsCorruptionException(
                    $"Published cloud WAL object '{proof.Segment.ObjectKey}' has a torn frame header.");
            }

            if (available < HeaderBytes)
            {
                return acknowledged ? 0 : HeaderBytes;
            }

            var header = window.Slice(cursor, HeaderBytes);
            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (payloadLength > DiskFormat.WalMaximumRecordBytes)
            {
                throw new PantsCorruptionException(
                    $"Published cloud WAL object '{proof.Segment.ObjectKey}' has a frame over the " +
                    "64 MiB limit.");
            }

            var frameLength = HeaderBytes + (long)payloadLength;
            if (frameLength > proof.Remaining)
            {
                throw new PantsCorruptionException(
                    $"Published cloud WAL object '{proof.Segment.ObjectKey}' has a torn frame payload.");
            }

            if (frameLength > available)
            {
                return acknowledged ? 0 : checked((int)frameLength);
            }

            var frame = window.Slice(cursor, checked((int)frameLength));
            var payload = frame[HeaderBytes..];
            if (DiskFormat.Crc32C(payload) != BinaryPrimitives.ReadUInt32LittleEndian(header[sizeof(uint)..]))
            {
                throw new PantsCorruptionException(
                    $"Published cloud WAL object '{proof.Segment.ObjectKey}' has a frame checksum mismatch.");
            }

            WalRecord record;
            try
            {
                record = WalCodec.DecodeRecord(payload);
            }
            catch (PantsException exception) when (exception is not PantsCorruptionException)
            {
                throw new PantsCorruptionException(
                    "A published cloud WAL frame is malformed.",
                    exception);
            }

            if (!proof.TryAcknowledge(frame, record, manifest.Coverage))
            {
                proof.UncoveredUnder = manifest.Identity;
                return 0;
            }

            acknowledged = true;
            cursor += frame.Length;
        }

        return 0;
    }
}
