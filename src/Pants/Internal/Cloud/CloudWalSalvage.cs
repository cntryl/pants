using System.Buffers.Binary;

namespace Pants;

static class CloudWalSalvage
{
    internal static ReadOnlyMemory<byte> CreateLocalRecoveryBytes(ReadOnlySpan<byte> bytes)
    {
        var cursor = 0;
        while (cursor < bytes.Length)
        {
            if (bytes.Length - cursor < 2 * sizeof(uint))
            {
                return bytes.ToArray();
            }

            var encodedPayloadLength = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.Slice(cursor, sizeof(uint)));
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.Slice(cursor + sizeof(uint), sizeof(uint)));
            cursor += 2 * sizeof(uint);
            if (encodedPayloadLength > MidgeDiskFormat.WalMaximumRecordBytes ||
                encodedPayloadLength > bytes.Length - cursor)
            {
                return bytes.ToArray();
            }

            var payloadLength = (int)encodedPayloadLength;
            var payload = bytes.Slice(cursor, payloadLength);
            if (MidgeDiskFormat.Crc32C(payload) != expectedCrc)
            {
                return bytes.ToArray();
            }

            try
            {
                _ = MidgeWalCodec.DecodeTransactionBatch(payload, out _);
            }
            catch (PantsException)
            {
                return bytes.ToArray();
            }

            cursor += payloadLength;
        }

        // The object disagrees with its catalog proof but contains only valid
        // frames. None of those frames can be trusted, so force a salvage stop
        // before applying the segment while retaining the remote bytes intact.
        return new byte[1];
    }
}
