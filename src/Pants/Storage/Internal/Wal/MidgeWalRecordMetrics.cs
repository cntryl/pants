namespace Cntryl.Pants.Storage.Internal.Wal;

static class MidgeWalRecordMetrics
{
    public static int GetLogicalByteCount(MidgeWalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return checked(
            record.Key.Length +
            (record.Value?.Length ?? 0) +
            (record.RangeEnd?.Length ?? 0));
    }
}
