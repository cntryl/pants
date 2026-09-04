namespace Cntryl.Pants.Transactions.Spill;

static class TransactionSpillTestHarness
{
    internal static ValueTask<IPantsDatabase> OpenAsync(
        SpillStorageMode mode,
        string path,
        long transactionMemoryPoolBytes = 1_024) =>
        PantsDatabase.OpenAsync(CreateOptions(mode, path, transactionMemoryPoolBytes));

    internal static PantsOpenOptions CreateOptions(
        SpillStorageMode mode,
        string path,
        long transactionMemoryPoolBytes = 1_024) =>
        (mode switch
        {
            SpillStorageMode.Local => PantsOpenOptions.Local(path),
            SpillStorageMode.Cloud => PantsOpenOptions.SimulatedCloud(
                path,
                "pants-tests",
                "transaction-spill/"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown storage mode.")
        })
        // The transaction pool remains deliberately tiny so these tests exercise spill.
        // The engine-wide budget must also accommodate a decoded SST block now that cloud
        // scans correctly read flushed data through the bounded scan pool.
        .WithMemoryBudget(PantsMemoryBudget.FromBytes(512 * 1_024))
        .WithMemtableLimits(2 * 1_024)
        .WithTransactionMemoryPool(transactionMemoryPoolBytes);

    internal static PantsWriteOptions GetWriteOptions(SpillStorageMode mode) =>
        mode == SpillStorageMode.Cloud
            ? PantsWriteOptions.CloudAsync
            : PantsWriteOptions.Buffered;

    internal static string[] FindArtifacts(string path) =>
        Directory.Exists(path)
            ? Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .Where(static file =>
                    file.EndsWith(".run", StringComparison.Ordinal) ||
                    file.EndsWith(".ranges", StringComparison.Ordinal))
                .ToArray()
            : [];

    internal static async Task<ReadOnlyMemory<byte>?> GetAsync(
        IPantsDatabase database,
        ReadOnlyMemory<byte> key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        return await transaction.GetAsync(key);
    }
}
