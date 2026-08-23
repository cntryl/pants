using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Benches.Tier2;

static class Tier2Database
{
    public static async ValueTask PutAsync(
        IPantsDatabase database,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        PantsWriteOptions writeOptions)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(key, value);
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
