using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Tier4;

static class Tier4Database
{
    public static PantsOpenOptions Options(string path, Tier4StorageMode mode) => mode switch
    {
        Tier4StorageMode.Memory => PantsOpenOptions.InMemory(),
        Tier4StorageMode.Local => PantsOpenOptions.Local(path),
        Tier4StorageMode.SimulatedCloud => CloudOptions(path),
        Tier4StorageMode.Hybrid => CloudOptions(path).WithSimulatedCloudLocalStorageBudget(64 * 1024 * 1024),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    public static PantsWriteOptions WriteOptions(Tier4StorageMode mode) => mode switch
    {
        Tier4StorageMode.Memory => PantsWriteOptions.BestEffort,
        Tier4StorageMode.Local => PantsWriteOptions.Buffered,
        Tier4StorageMode.SimulatedCloud or Tier4StorageMode.Hybrid => PantsWriteOptions.CloudAsync,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    public static async ValueTask PutBatchAsync(
        IPantsDatabase database,
        IEnumerable<(byte[] Key, byte[] Value)> entries,
        PantsWriteOptions writeOptions)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        foreach (var (key, value) in entries)
        {
            transaction.Put(key, value);
        }

        await transaction.CommitAsync(writeOptions);
    }

    public static async ValueTask<ReadOnlyMemory<byte>?> GetAsync(
        IPantsDatabase database,
        ReadOnlyMemory<byte> key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        return await transaction.GetAsync(key);
    }

    public static void DeletePath(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    static PantsOpenOptions CloudOptions(string path) => PantsOpenOptions.SimulatedCloud(
        path,
        "pants-benchmarks",
        $"tier4/{Path.GetFileName(path)}/");
}
