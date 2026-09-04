namespace Cntryl.Pants.Cloud.Internal;

static class CloudWalSalvage
{
    internal static ReadOnlyMemory<byte> CreateLocalRecoveryBytes(ReadOnlySpan<byte> bytes)
    {
        try
        {
            WalFrameReader.Visit(
                bytes,
                static (record, _) =>
                {
                    if (record.Operation == WalOperation.TransactionBatch)
                    {
                        WalCodec.ValidateTransactionBatch(record);
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
