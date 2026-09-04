using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsRetainedWalTombstoneRecoveryTests
{
    [Fact]
    public async Task ShouldNotResurrectManifestCoveredValueFromRetainedWalAfterTombstoneGc()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2, BackgroundEnabled: false));
        byte[] retainedWal;
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await using (var seed = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
            {
                seed.Put("a-anchor"u8.ToArray(), "a"u8.ToArray());
                seed.Put("target"u8.ToArray(), "stale"u8.ToArray());
                seed.Put("z-anchor"u8.ToArray(), "z"u8.ToArray());
                await seed.CommitAsync(PantsWriteOptions.Sync);
            }

            retainedWal = await File.ReadAllBytesAsync(Path.Combine(directory.Path, "wal", "wal.log"));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await using (var delete = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
            {
                delete.Delete("target"u8.ToArray());
                await delete.CommitAsync(PantsWriteOptions.Sync);
            }

            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await database.Maintenance.CompactAllAsync();
            var layout = await database.Diagnostics.GetStorageLayoutAsync();
            var files = layout.Levels.SelectMany(static level => level.Files).ToArray();
            Assert.NotEmpty(files);
            foreach (var file in files)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(directory.Path, "sst", file.Name));
                Assert.DoesNotContain(SstCodec.Decode(bytes).Entries,
                    static entry => entry.Key.AsSpan().SequenceEqual("target"u8));
            }
        }

        await File.WriteAllBytesAsync(
            Path.Combine(directory.Path, "wal", "00000000000000000000.wal"), retainedWal);
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var read = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);

        Assert.Null(await read.GetAsync("target"u8.ToArray()));
        Assert.Equal("a"u8.ToArray(), (await read.GetAsync("a-anchor"u8.ToArray()))?.ToArray());
        Assert.Equal("z"u8.ToArray(), (await read.GetAsync("z-anchor"u8.ToArray()))?.ToArray());
    }
}
