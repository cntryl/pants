namespace Cntryl.Pants.Support.TestDoubles;

static class TtlStorageModeTestHarness
{
    internal static ValueTask<IPantsDatabase> OpenAsync(
        TtlStorageMode mode,
        string path,
        IPantsClock clock) =>
        PantsDatabase.OpenAsync(CreateOptions(mode, path, clock));

    internal static PantsOpenOptions CreateOptions(
        TtlStorageMode mode,
        string path,
        IPantsClock clock) =>
        (mode switch
        {
            TtlStorageMode.Memory => PantsOpenOptions.InMemory(),
            TtlStorageMode.Local => PantsOpenOptions.Local(path),
            TtlStorageMode.Cloud => PantsOpenOptions.SimulatedCloud(
                path,
                "pants-tests",
                "ttl/"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown storage mode.")
        }).WithTtlClock(clock);

    internal static PantsWriteOptions GetWriteOptions(TtlStorageMode mode) =>
        mode == TtlStorageMode.Cloud
            ? PantsWriteOptions.CloudAsync
            : PantsWriteOptions.Buffered;

    internal static async Task PutAsync(
        IPantsDatabase database,
        TtlStorageMode mode,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        TimeSpan? timeToLive)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(key, value, timeToLive);
        await transaction.CommitAsync(GetWriteOptions(mode));
    }

    internal static async Task PutAsync(
        IPantsDatabase database,
        TtlStorageMode mode,
        string key,
        string value,
        TimeSpan? timeToLive) =>
        await PutAsync(
            database,
            mode,
            TestBytes.FromString(key),
            TestBytes.FromString(value),
            timeToLive);

    internal static async Task<ReadOnlyMemory<byte>?> GetAsync(
        IPantsDatabase database,
        ReadOnlyMemory<byte> key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        return await transaction.GetAsync(key);
    }

    internal static async Task<string?> GetTextAsync(IPantsDatabase database, string key)
    {
        var value = await GetAsync(database, TestBytes.FromString(key));
        return value is null ? null : TestBytes.ToText(value.Value);
    }
}
