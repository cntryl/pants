using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Benches.Tier3;

static class Tier3Database
{
    public static PantsOpenOptions Options(string path, Tier3StorageMode mode) => mode switch
    {
        Tier3StorageMode.Local => PantsOpenOptions.Local(path),
        Tier3StorageMode.SimulatedCloud => PantsOpenOptions.SimulatedCloud(
            path,
            "pants-benchmarks",
            $"tier3/{Path.GetFileName(path)}/"),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    public static PantsWriteOptions WriteOptions(Tier3StorageMode mode) => mode switch
    {
        Tier3StorageMode.Local => PantsWriteOptions.Buffered,
        Tier3StorageMode.SimulatedCloud => PantsWriteOptions.CloudAsync,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    public static async ValueTask PutBatchAsync(
        IPantsDatabase database,
        IEnumerable<(byte[] Key, byte[] Value)> entries,
        PantsWriteOptions writeOptions)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
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
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
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
}
