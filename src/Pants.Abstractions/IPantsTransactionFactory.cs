namespace Cntryl.Pants;

public interface IPantsTransactionFactory
{
    ValueTask<IPantsTransaction> BeginAsync(
        IPantsColumnFamily columnFamily,
        PantsTransactionMode mode,
        CancellationToken cancellationToken = default);
}
