namespace Cntryl.Pants;

public interface IPantsDatabase : IAsyncDisposable
{
    PantsOpenOptions Options { get; }

    PantsDatabaseCapabilities Capabilities { get; }

    IPantsColumnFamilyCatalog ColumnFamilies { get; }

    IPantsTransactionFactory Transactions { get; }

    IPantsDatabaseMaintenance Maintenance { get; }

    IPantsDatabaseDiagnostics Diagnostics { get; }

    IPantsPersistentStorage? PersistentStorage { get; }

    IPantsCloudDatabase? Cloud { get; }

    ValueTask ShutdownAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
