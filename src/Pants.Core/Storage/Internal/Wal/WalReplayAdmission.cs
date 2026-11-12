namespace Cntryl.Pants.Storage.Internal.Wal;

/// <summary>
///     Writer compatibility with Midge's configured cloud replay envelope. These are admission
///     bounds, not observations of .NET memory consumption or physical I/O.
/// </summary>
static class WalReplayAdmission
{
    // Midge's supported 64-bit targets: TxnBatchRecord + 2*WalRecord, 2*WalRecord,
    // and 2*(PendingTxn + (u64,u64)). Keep these distinct from .NET decoder accounting.
    const long ResidentOperationBytes = 416;
    const long PendingRecordBytes = 288;
    const long PendingTransactionBytes = 128;

    public static void Require(CommitPayload payload, PantsDurability durability, long? maximumBytes)
    {
        if (maximumBytes is not { } limit || payload.Operations.Count == 0 ||
            durability == PantsDurability.BestEffort && !payload.IsSpilled)
        {
            return;
        }

        var bytes = payload.IsSpilled ? PendingTransactionBytes : WalCodec.TransactionBatchPayloadOverhead;
        payload.Operations.ForEach(operation =>
        {
            bytes = checked(bytes + (payload.IsSpilled
                ? PendingRecordBytes + operation.Key.LongLength + (operation.Value?.LongLength ?? 0) +
                  (operation.EndExclusive?.LongLength ?? 0)
                : WalCodec.MeasureTransactionBatchOperation(operation.Key.Length, operation.Value?.Length,
                    operation.EndExclusive?.Length, operation.TimeToLive.HasValue || operation.ExpirationUnixMilliseconds.HasValue)));
        });
        if (!payload.IsSpilled)
        {
            WalCodec.RequireTransactionBatchPayloadFitsDecodedLimit(bytes);
            bytes = MeasureResident(bytes, (int)payload.Operations.Count);
        }
        RequireBytes(bytes, limit);
    }

    public static void RequireResident(IReadOnlyList<WalMutation> mutations, long? maximumBytes)
    {
        if (maximumBytes is { } limit)
        {
            RequireBytes(MeasureResident(WalCodec.MeasureTransactionBatchPayloadBytes(mutations), mutations.Count), limit);
        }
    }

    static long MeasureResident(long payloadBytes, int operationCount) => Math.Max(
        WalCodec.MeasureTransactionBatchFrameBound(payloadBytes),
        checked(2 * payloadBytes + operationCount * ResidentOperationBytes));

    static void RequireBytes(long bytes, long limit)
    {
        if (bytes > limit)
        {
            throw new PantsResourceLimitException(
                $"The transaction needs {bytes} WAL replay bytes; recovery replays at most {limit} " +
                "bytes with the configured cloud budgets.");
        }
    }
}
