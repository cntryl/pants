using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsLocalWalPruningTests
{
    const int ValueBytes = 64 * 1024;

    [Fact]
    public async Task ShouldBoundWalBytesWhenColdFamilyHoldsUnflushedWrite()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        var cold = await database.ColumnFamilies.CreateAsync("cold");
        await PutAsync(database, cold, "cold-key", 16);

        for (var cycle = 0; cycle < 6; cycle++)
        {
            await PutAsync(database, database.ColumnFamilies.DefaultFamily, $"hot-{cycle}", ValueBytes);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        Assert.True(
            WalBytes(directory.Path) < 2L * ValueBytes,
            $"WAL retained {WalBytes(directory.Path)} bytes of flushed data.");
    }

    [Fact]
    public async Task ShouldRecoverUncoveredWritesAfterReopenWhenCoveredSegmentsWerePruned()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            var cold = await database.ColumnFamilies.CreateAsync("cold");
            await PutAsync(database, cold, "gone", 16);
            await using (var transaction = await database.Transactions.BeginAsync(
                             cold,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Delete(TestBytes.FromString("gone"));
                transaction.Put(TestBytes.FromString("kept"), TestBytes.FromString("cold-value"));
                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }

            for (var cycle = 0; cycle < 3; cycle++)
            {
                await PutAsync(database, database.ColumnFamilies.DefaultFamily, $"hot-{cycle}", 1024);
                await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            }
        }

        await using var reopened = await OpenAsync(directory.Path);
        var coldReopened = (await reopened.ColumnFamilies.GetAsync("cold"))!;
        await using var reader = await reopened.Transactions.BeginAsync(
            coldReopened,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync(TestBytes.FromString("gone")));
        Assert.Equal(
            "cold-value",
            TestBytes.ToText((await reader.GetAsync(TestBytes.FromString("kept")))!.Value));
        await using var hot = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.NotNull(await hot.GetAsync(TestBytes.FromString("hot-0")));
        Assert.NotNull(await hot.GetAsync(TestBytes.FromString("hot-2")));
    }

    static long WalBytes(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "wal"))
            .Sum(static path => new FileInfo(path).Length);

    static async Task PutAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key,
        int valueBytes)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), System.Security.Cryptography.RandomNumberGenerator.GetBytes(valueBytes));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static ValueTask<IPantsDatabase> OpenAsync(string path) =>
        PantsDatabase.OpenAsync(PantsOpenOptions.Local(path).WithBackgroundCompaction(false));
}
