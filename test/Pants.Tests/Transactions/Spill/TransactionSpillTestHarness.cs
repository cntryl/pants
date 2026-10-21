namespace Cntryl.Pants.Transactions.Spill;

static class TransactionSpillTestHarness
{
    const long DefaultMemoryBudgetBytes = 8 * 1_024 * 1_024;
    const long LocalMemtableSizeLimitBytes = 64 * 1_024;
    const long CloudMemtableSizeLimitBytes = 2 * 1_024 * 1_024;

    internal static ValueTask<IPantsDatabase> OpenAsync(
        SpillStorageMode mode,
        string path,
        long transactionMemoryPoolBytes = 1_024,
        long? memtableSizeLimitBytes = null,
        long memoryBudgetBytes = DefaultMemoryBudgetBytes) =>
        PantsDatabase.OpenAsync(CreateOptions(
            mode,
            path,
            transactionMemoryPoolBytes,
            memtableSizeLimitBytes,
            memoryBudgetBytes));

    internal static PantsOpenOptions CreateOptions(
        SpillStorageMode mode,
        string path,
        long transactionMemoryPoolBytes = 1_024,
        long? memtableSizeLimitBytes = null,
        long memoryBudgetBytes = DefaultMemoryBudgetBytes)
    {
        var storageOptions = mode switch
        {
            SpillStorageMode.Local => PantsOpenOptions.Local(path),
            SpillStorageMode.Cloud => PantsOpenOptions.SimulatedCloud(
                path,
                "pants-tests",
                "transaction-spill/"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown storage mode.")
        };
        var memtableTargetBytes = memtableSizeLimitBytes ?? mode switch
        {
            SpillStorageMode.Local => LocalMemtableSizeLimitBytes,
            SpillStorageMode.Cloud => CloudMemtableSizeLimitBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown storage mode.")
        };

        return storageOptions
        // The transaction pool remains deliberately tiny so these tests exercise spill.
        // Midge uses a 64 KiB local memtable and a 2 MiB cloud memtable for these fixtures.
        // Eight MiB leaves room for both memtable generations and the bounded worker pools.
        .WithMemoryBudget(PantsMemoryBudget.FromBytes(memoryBudgetBytes))
        .WithMemtableLimits(memtableTargetBytes)
        .WithTransactionMemoryPool(transactionMemoryPoolBytes);
    }

    internal static PantsWriteOptions GetWriteOptions(SpillStorageMode mode) =>
        mode == SpillStorageMode.Cloud
            ? PantsWriteOptions.CloudAsync
            : PantsWriteOptions.Buffered;

    internal static PantsWriteOptions GetSyncWriteOptions(SpillStorageMode mode) =>
        mode == SpillStorageMode.Cloud
            ? PantsWriteOptions.CloudStrict
            : PantsWriteOptions.Sync;

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
