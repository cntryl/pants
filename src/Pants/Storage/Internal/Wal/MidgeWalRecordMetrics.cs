namespace Cntryl.Pants;

internal static class MidgeWalRecordMetrics
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
