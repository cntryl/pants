using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeManifestCheckpointParityTests
{
    const int Rounds = 3;
    const int RowsPerRound = 4;

    [Fact]
    public async Task ShouldPreserveEveryFlushedRowWhenManifestCheckpointsAdvanceThroughCompaction()
    {
        foreach (var simulatedCloud in new[] { false, true })
        {
            using var directory = new TemporaryDirectory();
            var options = OptionsFor(directory.Path, simulatedCloud);
            ulong previousCheckpoint = 0;

            await using (var database = await PantsDatabase.OpenAsync(options))
            {
                var family = await database.ColumnFamilies.CreateAsync("data");
                var writeOptions = simulatedCloud
                    ? PantsWriteOptions.CloudStrict
                    : PantsWriteOptions.Sync;

                for (var round = 0; round < Rounds; round++)
                {
                    for (var index = 0; index < RowsPerRound; index++)
                    {
                        await using var transaction = await database.Transactions.BeginAsync(
                            family,
                            PantsTransactionMode.ReadWrite);
                        transaction.Put(
                            KeyBytes(round, index),
                            ValueBytes(round, index));
                        await transaction.CommitAsync(writeOptions);
                        await database.Maintenance.FlushAsync(family);
                    }

                    var flushedCheckpoint = await ReadCheckpointIdAsync(directory.Path);
                    Assert.True(
                        flushedCheckpoint >= previousCheckpoint,
                        $"Manifest checkpoint regressed in simulatedCloud={simulatedCloud}.");
                    var compactionsBefore =
                        (await database.Diagnostics.GetRuntimeMetricsAsync()).CompactionsRun;

                    await database.Maintenance.CompactAllAsync();

                    var compactedCheckpoint = await ReadCheckpointIdAsync(directory.Path);
                    Assert.True(
                        compactedCheckpoint > flushedCheckpoint,
                        $"Compaction did not advance the manifest checkpoint in simulatedCloud={simulatedCloud}.");
                    Assert.True(
                        (await database.Diagnostics.GetRuntimeMetricsAsync()).CompactionsRun > compactionsBefore);
                    previousCheckpoint = compactedCheckpoint;
                    await AssertRowsAsync(database, round + 1, family);
                }

                await database.ShutdownAsync(TestTimeouts.Expected);
            }

            await using var reopened = await PantsDatabase.OpenAsync(options);
            Assert.True(await ReadCheckpointIdAsync(directory.Path) >= previousCheckpoint);
            var reopenedFamily = Assert.IsAssignableFrom<IPantsColumnFamily>(
                await reopened.ColumnFamilies.GetAsync("data"));
            await AssertRowsAsync(reopened, Rounds, reopenedFamily);
        }
    }

    static PantsOpenOptions OptionsFor(string path, bool simulatedCloud) =>
        (simulatedCloud
            ? PantsOpenOptions.SimulatedCloud(path, "pants-tests", "manifest-checkpoint/")
            : PantsOpenOptions.Local(path))
        .WithBackgroundCompaction(false);

    static async Task<ulong> ReadCheckpointIdAsync(string root)
    {
        using var manifest = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(root, "manifest.snapshot.json")));
        return manifest.RootElement.GetProperty("edit_checkpoint_id").GetUInt64();
    }

    static async Task AssertRowsAsync(
        IPantsDatabase database,
        int rounds,
        IPantsColumnFamily family)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        var rows = new List<(string Key, string Value)>();
        await foreach (var row in scan)
        {
            rows.Add((TestBytes.ToText(row.Key), TestBytes.ToText(row.Value)));
        }

        var expected = Enumerable.Range(0, rounds)
            .SelectMany(round => Enumerable.Range(0, RowsPerRound)
                .Select(index => ($"key-{round}-{index}", $"value-{round}-{index}")))
            .ToArray();
        Assert.Equal(expected.Length, rows.Count);
        Assert.Equal(expected, rows);
    }

    static byte[] KeyBytes(int round, int index) =>
        TestBytes.FromString($"key-{round}-{index}");

    static byte[] ValueBytes(int round, int index) =>
        TestBytes.FromString($"value-{round}-{index}");
}
