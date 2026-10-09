using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

/// <summary>
///     Compaction's output partition target must leave room for the decoded blocks its inputs hold,
///     so a default four-file L0 fan-in can compact under a small maintenance pool.
/// </summary>
public sealed class PantsCompactionPartitionSizingTests
{
    const int FlushCount = 4;
    const int EntriesPerFlush = 8;
    const int ValueBytes = 8 * 1024;

    [Fact]
    public async Task ShouldCompactAFourFileL0FanInUnderATwoMebibyteBudget()
    {
        using var directory = new TemporaryDirectory();
        var options = OpenOptions(directory.Path, 2L * 1024 * 1024);
        await WriteFlushesAsync(options);

        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var inputs = SstFiles(directory.Path);
            Assert.Equal(FlushCount, inputs.Length);

            await database.Maintenance.CompactAllAsync();

            var outputs = SstFiles(directory.Path);
            Assert.NotEmpty(outputs);
            Assert.Empty(outputs.Intersect(inputs, StringComparer.Ordinal));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertAllKeysReadableAsync(reopened);
    }

    [Fact]
    public async Task ShouldKeepEveryInputWhenTheFanInCannotFitAMinimalOutputPartition()
    {
        using var directory = new TemporaryDirectory();
        var options = OpenOptions(directory.Path, 1L * 1024 * 1024);
        await WriteFlushesAsync(options);
        var originals = Directory.GetFiles(SstDirectory(directory.Path), "*.sst")
            .Order(StringComparer.Ordinal)
            .ToArray();

        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
                database.Maintenance.CompactAllAsync().AsTask());
        }

        Assert.Equal(
            originals,
            Directory.GetFiles(SstDirectory(directory.Path), "*.sst").Order(StringComparer.Ordinal));
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertAllKeysReadableAsync(reopened);
    }

    static PantsOpenOptions OpenOptions(string path, long budgetBytes) =>
        PantsOpenOptions.Local(path)
            .WithBackgroundCompaction(false)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budgetBytes))
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: 100,
                BackgroundEnabled: false));

    // Flush f writes every FlushCount-th key starting at f, so the four L0 files interleave across
    // the whole key space: each input spans the full range and holds a block the merge keeps live,
    // while the merged output is large enough to fill an output partition.
    static async Task WriteFlushesAsync(PantsOpenOptions options)
    {
        await using var database = await PantsDatabase.OpenAsync(options);
        for (var flush = 0; flush < FlushCount; flush++)
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            for (var entry = 0; entry < EntriesPerFlush; entry++)
            {
                var index = (entry * FlushCount) + flush;
                transaction.Put(Key(index), Value(index));
            }

            await transaction.CommitAsync(PantsWriteOptions.Buffered);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }
    }

    static async Task AssertAllKeysReadableAsync(IPantsDatabase database)
    {
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < FlushCount * EntriesPerFlush; index++)
        {
            var read = await reader.GetAsync(Key(index));
            Assert.Equal(Value(index), read?.ToArray());
        }
    }

    static string[] SstFiles(string root) =>
        Directory.GetFiles(SstDirectory(root), "*.sst").Order(StringComparer.Ordinal).ToArray();

    static string SstDirectory(string root) => Path.Combine(root, "sst");

    static byte[] Key(int index) => TestBytes.FromString($"k-{index:D3}");

    // Seeded, incompressible payloads keep each decoded input block near the 64 KiB block target.
    static byte[] Value(int index)
    {
        var value = new byte[ValueBytes];
        new Random(index).NextBytes(value);
        return value;
    }
}
