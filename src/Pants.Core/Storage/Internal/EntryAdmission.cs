namespace Cntryl.Pants.Storage.Internal;

/// <summary>
///     Rejects a staged operation that some layer carrying it could not later write down.
/// </summary>
/// <remarks>
///     An operation travels through the WAL, then flush and compaction into SST blocks, then replay
///     on reopen. Admitting one that fits some of those layers but not all acknowledges a commit that
///     leaves the database unable to flush or reopen, so admission takes the tightest bound.
/// </remarks>
static class EntryAdmission
{
    public static void ValidatePointWrite(int keyLength, int? valueLength)
    {
        SstCodec.ValidateEntrySize(keyLength, valueLength ?? 0);
        ValidateWalRecord(WalCodec.MeasureWorstCaseOperationRecord(keyLength, valueLength, null));
    }

    public static void ValidateRangeDelete(int startLength, int endLength)
    {
        SstCodec.ValidateRangeTombstoneSize(startLength, endLength);
        ValidateWalRecord(WalCodec.MeasureWorstCaseOperationRecord(startLength, null, endLength));
    }

    static void ValidateWalRecord(long recordBytes)
    {
        if (recordBytes > DiskFormat.WalMaximumRecordBytes)
        {
            throw new PantsResourceLimitException(
                $"The operation encodes to a {recordBytes}-byte WAL record, which exceeds the " +
                $"{DiskFormat.WalMaximumRecordBytes}-byte frame limit.");
        }
    }
}
