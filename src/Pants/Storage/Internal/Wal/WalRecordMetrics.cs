namespace Cntryl.Pants.Storage.Internal.Wal;

static class WalRecordMetrics
{
    public static int GetLogicalByteCount(WalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return checked(
            record.Key.Length +
            (record.Value?.Length ?? 0) +
            (record.RangeEnd?.Length ?? 0));
    }
}
