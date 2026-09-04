namespace Cntryl.Pants;

public interface IPantsColumnFamilyCatalog
{
    IPantsColumnFamily DefaultFamily { get; }

    ValueTask<IPantsColumnFamily> CreateAsync(
        string name,
        CancellationToken cancellationToken = default);

    ValueTask<IPantsColumnFamily?> GetAsync(
        string name,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<IPantsColumnFamily>> ListAsync(
        CancellationToken cancellationToken = default);

    ValueTask DropAsync(
        IPantsColumnFamily columnFamily,
        CancellationToken cancellationToken = default);

    ValueTask DropDiscardingUnflushedAsync(
        IPantsColumnFamily columnFamily,
        CancellationToken cancellationToken = default);
}
