using Cntryl.Pants.Support.TestDoubles;
using Cntryl.Pants.Transactions.Spill;

namespace Cntryl.Pants.Transactions;

public sealed class PantsRangeDeleteConflictRetentionTests
{
    [Fact]
    public async Task ShouldRejectPointWriteGivenCoveringRangeDeleteEvictedFromRecentWindow()
    {
        await ForEachStageAsync(async (database, mode, stage, _) =>
        {
            var family = database.ColumnFamilies.DefaultFamily;
            await SeedAsync(database, family, mode);
            if (stage != "memtable")
            {
                await database.Maintenance.FlushAsync(family);
            }

            await using var stale = await database.Transactions.BeginAsync(
                family,
                PantsTransactionMode.ReadWrite);
            stale.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
            stale.Put("key-b"u8.ToArray(), "from-tx"u8.ToArray());
            await CommitCoveringRangeDeleteAsync(database, family, mode);
            await MoveTombstoneToStageAsync(database, family, stage);

            var conflict = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
                stale.CommitAsync(WriteOptions(mode)).AsTask());

            Assert.False(conflict.IsRangeConflict);
        });
    }

    [Fact]
    public async Task ShouldRejectRangeDeleteGivenOverlappingRangeDeleteEvictedFromRecentWindow()
    {
        await ForEachStageAsync(async (database, mode, stage, _) =>
        {
            var family = database.ColumnFamilies.DefaultFamily;
            await SeedAsync(database, family, mode);
            if (stage != "memtable")
            {
                await database.Maintenance.FlushAsync(family);
            }

            await using var stale = await database.Transactions.BeginAsync(
                family,
                PantsTransactionMode.ReadWrite);
            stale.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
            stale.DeleteRange("key-c"u8.ToArray(), "key-d"u8.ToArray());
            await CommitCoveringRangeDeleteAsync(database, family, mode);
            await MoveTombstoneToStageAsync(database, family, stage);

            var conflict = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
                stale.CommitAsync(WriteOptions(mode)).AsTask());

            Assert.True(conflict.IsRangeConflict);
        });
    }

    [Fact]
    public async Task ShouldRejectAssertionGivenCoveringRangeDeleteEvictedFromRecentWindow()
    {
        await ForEachStageAsync(async (database, mode, stage, _) =>
        {
            var family = database.ColumnFamilies.DefaultFamily;
            await SeedAsync(database, family, mode);
            if (stage != "memtable")
            {
                await database.Maintenance.FlushAsync(family);
            }

            await using var stale = await database.Transactions.BeginAsync(
                family,
                PantsTransactionMode.ReadWrite);
            stale.AssertValue("key-b"u8.ToArray(), "seed"u8.ToArray());
            stale.Put("unrelated"u8.ToArray(), "value"u8.ToArray());
            await CommitCoveringRangeDeleteAsync(database, family, mode);
            await MoveTombstoneToStageAsync(database, family, stage);

            var conflict = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
                stale.CommitAsync(WriteOptions(mode)).AsTask());

            Assert.False(conflict.IsRangeConflict);
        });
    }

    [Fact]
    public async Task ShouldRejectSpilledCommitGivenCoveringRangeDeleteEvictedFromRecentWindow()
    {
        await ForEachStageAsync(async (database, mode, stage, databasePath) =>
        {
            var family = database.ColumnFamilies.DefaultFamily;
            await SeedAsync(database, family, mode);
            if (stage != "memtable")
            {
                await database.Maintenance.FlushAsync(family);
            }

            await using var stale = await database.Transactions.BeginAsync(
                family,
                PantsTransactionMode.ReadWrite);
            stale.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
            stale.Put("key-b"u8.ToArray(), "from-tx"u8.ToArray());
            for (var index = 0; index < 200; index++)
            {
                stale.Put(
                    TestBytes.FromString($"spill-key-{index:0000}"),
                    TestBytes.FromString($"spill-value-{index:0000}"));
            }

            Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(databasePath));
            await CommitCoveringRangeDeleteAsync(database, family, mode);
            await MoveTombstoneToStageAsync(database, family, stage);

            var conflict = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
                stale.CommitAsync(WriteOptions(mode)).AsTask());

            Assert.False(conflict.IsRangeConflict);
        }, includeMemory: false, spill: true);
    }

    static async Task ForEachStageAsync(
        Func<IPantsDatabase, string, string, string, Task> scenario,
        bool includeMemory = true,
        bool spill = false)
    {
        var modes = spill
            ? new[] { "local", "cloud" }
            : includeMemory
                ? new[] { "memory", "local", "cloud" }
                : new[] { "local", "cloud" };
        foreach (var mode in modes)
        {
            var stages = mode == "memory"
                ? new[] { "memtable" }
                : new[] { "memtable", "flushed", "compacted" };
            foreach (var stage in stages)
            {
                using var directory = new TemporaryDirectory();
                var databasePath = Path.Combine(directory.Path, "database");
                await using var database = await PantsDatabase.OpenAsync(
                    CreateOptions(mode, databasePath, spill));
                await scenario(database, mode, stage, databasePath);
            }
        }
    }

    static PantsOpenOptions CreateOptions(string mode, string path, bool spill)
    {
        if (spill)
        {
            PantsStorageConfiguration storage = mode == "cloud"
                ? new PantsStorageConfiguration.SimulatedCloud(path, "pants-tests", "range-conflict/")
                : new PantsStorageConfiguration.Local(path);
            return PantsOpenOptions.Create(
                    storage,
                    memory: PantsMemoryConfiguration.Default with
                    {
                        Budget = PantsMemoryBudget.FromBytes(8 * 1_024 * 1_024),
                        MemtableSizeLimitBytes = 2 * 1_024 * 1_024,
                        TransactionMemoryPoolBytes = 1_024
                    })
                .WithBackgroundCompaction(false);
        }

        return mode switch
        {
            "memory" => PantsOpenOptions.InMemory(),
            "local" => PantsOpenOptions.Local(path).WithBackgroundCompaction(false),
            "cloud" => PantsOpenOptions.SimulatedCloud(path, "pants-tests", "range-conflict/")
                .WithBackgroundCompaction(false),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown storage mode.")
        };
    }

    static async Task SeedAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string mode)
    {
        await using var seed = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        seed.Put("key-b"u8.ToArray(), "seed"u8.ToArray());
        await seed.CommitAsync(WriteOptions(mode));
    }

    static async Task CommitCoveringRangeDeleteAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string mode)
    {
        await using var concurrent = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        concurrent.DeleteRange("key-a"u8.ToArray(), "key-z"u8.ToArray());
        await concurrent.CommitAsync(WriteOptions(mode));
    }

    static async Task MoveTombstoneToStageAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string stage)
    {
        if (stage == "flushed")
        {
            await database.Maintenance.FlushAsync(family);
        }
        else if (stage == "compacted")
        {
            await database.Maintenance.FlushAsync(family);
            await database.Maintenance.CompactAllAsync();
        }
    }

    static PantsWriteOptions WriteOptions(string mode) =>
        mode == "cloud" ? PantsWriteOptions.CloudAsync : PantsWriteOptions.Buffered;
}
