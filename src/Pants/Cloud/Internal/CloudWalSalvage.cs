namespace Pants;

static class CloudWalSalvage
{
    internal static ReadOnlyMemory<byte> CreateLocalRecoveryBytes(ReadOnlySpan<byte> bytes)
    {
        try
        {
            MidgeWalFrameReader.Visit(
                bytes,
                static (record, _) =>
                {
                    if (record.Operation == MidgeWalOperation.TransactionBatch)
                    {
                        MidgeWalCodec.ValidateTransactionBatch(record);
                    }
                });
        }
        catch (PantsException)
        {
            return bytes.ToArray();
        }

        // The object disagrees with its catalog proof but contains only valid
        // frames. None of those frames can be trusted, so force a salvage stop
        // before applying the segment while retaining the remote bytes intact.
        return new byte[1];
    }
}
