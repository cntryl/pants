using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsDirectoryEntryDurabilityTests
{
    [Theory]
    [InlineData(nameof(Failpoint.BeforeDatabaseRootDirectorySync))]
    [InlineData(nameof(Failpoint.BeforeWalDirectorySync))]
    [InlineData(nameof(Failpoint.BeforeWalFileDirectorySync))]
    [InlineData(nameof(Failpoint.BeforeFlushStagingDirectorySync))]
    public async Task ShouldNotCompleteOpenBeforeNewStorageNamesReachDirectorySync(
        string failpointName)
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "database");
        var options = PantsOpenOptions.Local(root).WithBackgroundCompaction(false);
        var walDirectory = Path.Combine(root, "wal");
        var walPath = Path.Combine(walDirectory, "wal.log");
        if (failpointName != nameof(Failpoint.BeforeDatabaseRootDirectorySync))
        {
            await using (var initialized = await PantsDatabase.OpenAsync(options))
            {
            }

            if (failpointName == nameof(Failpoint.BeforeWalDirectorySync))
            {
                Directory.Delete(walDirectory, recursive: true);
            }
            else if (failpointName == nameof(Failpoint.BeforeWalFileDirectorySync))
            {
                File.Delete(walPath);
            }
            else
            {
                Directory.Delete(Path.Combine(root, "sst", ".flush-staging"), recursive: true);
            }
        }

        var handler = new ThrowAtFailpointHandler(Enum.Parse<Failpoint>(failpointName));

        await Assert.ThrowsAnyAsync<PantsException>(() => PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(handler)).AsTask());

        Assert.Equal(1, handler.HitCount);
        Assert.True(Directory.Exists(root));
        if (failpointName == nameof(Failpoint.BeforeWalFileDirectorySync))
        {
            Assert.True(File.Exists(walPath));
        }

        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("durable"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "value",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("durable"u8.ToArray()))));
    }

    sealed class ThrowAtFailpointHandler(Failpoint target) : IFailpointHandler
    {
        int _hitCount;

        public int HitCount => Volatile.Read(ref _hitCount);

        public void Hit(Failpoint failpoint)
        {
            if (failpoint == target && Interlocked.Increment(ref _hitCount) == 1)
            {
                throw new IOException($"Injected failure at {failpoint}.");
            }
        }
    }
}
