namespace Cntryl.Pants.Support.TestDoubles;

static class StorageModeTestHarness
{
    public static ValueTask<IPantsDatabase> OpenAsync(string mode, string path) =>
        PantsDatabase.OpenAsync(mode switch
        {
            "memory" => PantsOpenOptions.InMemory(),
            "local" => PantsOpenOptions.Local(path),
            "cloud" => PantsOpenOptions.SimulatedCloud(path, "pants-tests", "baseline/"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown storage mode.")
        });

    public static PantsWriteOptions GetWriteOptions(string mode) =>
        mode == "cloud" ? PantsWriteOptions.CloudAsync : PantsWriteOptions.Buffered;

    public static async Task PutAsync(
        IPantsDatabase database,
        string mode,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(key, value);
        await transaction.CommitAsync(GetWriteOptions(mode));
    }

    public static async Task PutAsync(
        IPantsDatabase database,
        string mode,
        string key,
        string value) =>
        await PutAsync(database, mode, TestBytes.FromString(key), TestBytes.FromString(value));

    public static async Task<ReadOnlyMemory<byte>?> GetAsync(
        IPantsDatabase database,
        ReadOnlyMemory<byte> key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        return await transaction.GetAsync(key);
    }

    public static async Task<string?> GetTextAsync(IPantsDatabase database, string key)
    {
        var value = await GetAsync(database, TestBytes.FromString(key));
        return value is null ? null : TestBytes.ToText(value.Value);
    }
}
