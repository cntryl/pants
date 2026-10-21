using Cntryl.Pants.Support;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsColdFamilyTombstoneTests
{
    const int ChunkCount = 6;
    const int OperationsPerPhase = 160;
    const int OperationsPerTransaction = 16;
    const int KeySpace = 48;

    [Fact]
    public async Task ShouldPreserveColdTombstoneGivenHotColdCompactionWhenReopening()
    {
        using var directory = new TemporaryDirectory();
        var writeOptions = PantsWriteOptions.Sync;
        var hotModel = new Dictionary<string, string>(StringComparer.Ordinal);
        var coldModel = new Dictionary<string, string>(StringComparer.Ordinal);

        await using (var database = await PantsDatabase.OpenAsync(CreateOptions(directory.Path)))
        {
            var hot = await database.ColumnFamilies.CreateAsync("hot");
            var cold = await database.ColumnFamilies.CreateAsync("cold");

            var observedPersistedColdTombstone = await RunChunksAsync(
                database,
                hot,
                cold,
                hotModel,
                coldModel,
                writeOptions);
            Assert.True(
                observedPersistedColdTombstone,
                "The workload must delete a cold-family key that was already flushed.");
            await database.Maintenance.CompactAllAsync();

            await AssertFamilyMatchesModelAsync(database, hot, hotModel);
            await AssertFamilyMatchesModelAsync(database, cold, coldModel);
            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var reopened = await PantsDatabase.OpenAsync(CreateOptions(directory.Path));
        var hotReopened = await reopened.ColumnFamilies.GetAsync("hot");
        var coldReopened = await reopened.ColumnFamilies.GetAsync("cold");
        Assert.NotNull(hotReopened);
        Assert.NotNull(coldReopened);

        await AssertFamilyMatchesModelAsync(reopened, hotReopened, hotModel);
        await AssertFamilyMatchesModelAsync(reopened, coldReopened, coldModel);
        Assert.NotEmpty(coldModel);
    }

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.Local(path)
        .WithBackgroundCompaction(false)
        .WithCompaction(new PantsCompactionConfiguration(
            L0FileCountTrigger: 2,
            MaximumInputFiles: 2,
            L1TargetSizeBytes: 1,
            MaximumLevels: 4,
            BackgroundEnabled: false));

    static async Task<bool> RunChunksAsync(
        IPantsDatabase database,
        IPantsColumnFamily hot,
        IPantsColumnFamily cold,
        Dictionary<string, string> hotModel,
        Dictionary<string, string> coldModel,
        PantsWriteOptions writeOptions)
    {
        // Two lanes own disjoint families, so each family's model is fixed by its own lane's
        // seeded sequence. The barrier pins the interleaving: writes and flushes run concurrently
        // in phase A, then compaction runs while the cold lane keeps writing tombstones in phase B.
        using var barrier = new Barrier(2);
        var observedPersistedColdTombstone = false;
        var hotLane = Task.Run(async () =>
        {
            var random = new Random(1);
            for (var chunk = 0; chunk < ChunkCount; chunk++)
            {
                await ApplyOperationsAsync(database, hot, hotModel, random, "hot", chunk, 0, writeOptions);
                await database.Maintenance.FlushAsync(hot);
                WaitForLane(barrier);

                await database.Maintenance.CompactAllAsync();
                WaitForLane(barrier);
            }
        });
        var coldLane = Task.Run(async () =>
        {
            var random = new Random(2);
            var persistedColdKeys = new HashSet<string>(StringComparer.Ordinal);
            var deletedPersistedColdKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var chunk = 0; chunk < ChunkCount; chunk++)
            {
                await ApplyOperationsAsync(database, cold, coldModel, random, "cold", chunk, 0, writeOptions);
                await database.Maintenance.FlushAsync(cold);
                persistedColdKeys.Clear();
                persistedColdKeys.UnionWith(coldModel.Keys);
                WaitForLane(barrier);

                deletedPersistedColdKeys.Clear();
                await ApplyOperationsAsync(
                    database,
                    cold,
                    coldModel,
                    random,
                    "cold",
                    chunk,
                    1,
                    writeOptions,
                    persistedColdKeys,
                    deletedPersistedColdKeys);
                await database.Maintenance.FlushAsync(cold);
                observedPersistedColdTombstone |= deletedPersistedColdKeys.Any(
                    key => !coldModel.ContainsKey(key));
                WaitForLane(barrier);
            }
        });

        await Task.WhenAll(hotLane, coldLane);
        return observedPersistedColdTombstone;
    }

    static async Task ApplyOperationsAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        Dictionary<string, string> model,
        Random random,
        string lane,
        int chunk,
        int phase,
        PantsWriteOptions writeOptions,
        HashSet<string>? persistedKeys = null,
        HashSet<string>? deletedPersistedKeys = null)
    {
        for (var start = 0; start < OperationsPerPhase; start += OperationsPerTransaction)
        {
            await using var transaction = await database.Transactions.BeginAsync(
                family,
                PantsTransactionMode.ReadWrite);
            for (var offset = start; offset < start + OperationsPerTransaction; offset++)
            {
                var key = $"key-{random.Next(KeySpace):000}";
                // Deletes are roughly one in three, so cold keys are repeatedly tombstoned after
                // earlier flushes and compactions have pushed their values into deeper levels.
                if (random.Next(3) == 0)
                {
                    transaction.Delete(TestBytes.FromString(key));
                    if (persistedKeys?.Contains(key) == true)
                    {
                        deletedPersistedKeys?.Add(key);
                    }

                    model.Remove(key);
                }
                else
                {
                    var value = $"{lane}-{chunk}-{phase}-{offset}";
                    transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
                    model[key] = value;
                }
            }

            await transaction.CommitAsync(writeOptions);
        }
    }

    static void WaitForLane(Barrier barrier)
    {
        if (!barrier.SignalAndWait(TestTimeouts.Expected))
        {
            throw new TimeoutException("Compaction interleaving barrier did not complete.");
        }
    }

    static async Task AssertFamilyMatchesModelAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        IReadOnlyDictionary<string, string> model)
    {
        for (var index = 0; index < KeySpace; index++)
        {
            var key = $"key-{index:000}";
            Assert.Equal(model.GetValueOrDefault(key), await ReadAsync(database, family, key));
        }

        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        var scanned = new List<(string Key, string Value)>();
        await foreach (var entry in scan)
        {
            scanned.Add((TestBytes.ToText(entry.Key), TestBytes.ToText(entry.Value)));
        }

        Assert.Equal(
            model.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => (pair.Key, pair.Value)),
            scanned);
    }

    static async Task<string?> ReadAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(TestBytes.FromString(key));
        return value is null ? null : TestBytes.ToText(value.Value);
    }
}
