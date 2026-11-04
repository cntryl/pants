using Cntryl.Pants.Storage.Internal.Wal;

namespace Cntryl.Pants.Storage.Internal;

sealed record SealedWalSegment(
    ulong SegmentId,
    ulong WriterEpoch,
    ulong MaximumSequence,
    string FileName,
    byte[] Bytes)
{
    public WalOpenFacts Header => WalSegmentCodec.DecodeHeader(Bytes).Open;

    public WalSealedFacts Facts
    {
        get
        {
            WalFrameReader.Visit(Bytes, static (_, _) => { });
            var cursor = WalSegmentCodec.HeaderSize;
            while (cursor < Bytes.Length)
            {
                var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(cursor));
                var payload = Bytes.AsSpan(cursor + 8, checked((int)length));
                if (WalSegmentCodec.IsFooter(payload))
                {
                    var rows = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload[43..]);
                    var maxima = new ulong[rows][];
                    for (var index = 0; index < rows; index++)
                    {
                        maxima[index] = [System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload[(47 + index * 12)..]),
                            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload[(51 + index * 12)..])];
                    }

                    return new WalSealedFacts(new WalChainLink(SegmentId, MaximumSequence, payload[^16..].ToArray()),
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload[11..]), checked((ulong)cursor), maxima);
                }

                cursor += checked((int)length + 8);
            }

            throw new PantsCorruptionException("Published WAL segment is not sealed.");
        }
    }
}
