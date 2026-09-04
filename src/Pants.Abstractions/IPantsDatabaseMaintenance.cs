namespace Cntryl.Pants;

public interface IPantsDatabaseMaintenance
{
    ValueTask FlushAsync(
        IPantsColumnFamily columnFamily,
        CancellationToken cancellationToken = default);

    ValueTask CompactAllAsync(CancellationToken cancellationToken = default);

    ValueTask SetBackgroundCompactionAsync(
        bool enabled,
        CancellationToken cancellationToken = default);

    ValueTask<bool> WaitForWriteStallClearAsync(
        IPantsColumnFamily columnFamily,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
