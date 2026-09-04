using System.Text.Json;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsCompactionConflictRecoveryTests
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public async Task ShouldPreserveInputsAndRemovePartialOutputsWhenConflictingVersionsAbortCompaction()
    {
        using var directory = new TemporaryDirectory();
        var sstDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "sst"));
        var manifest = new ManifestState
        {
            LastPersistedSequence = 7,
            NextSstSeqs = new Dictionary<uint, ulong> { [0] = 3 },
            ColumnFamilies = [new ColumnFamilyMeta { Id = 0, Name = "default" }]
        };
        var inputs = new Dictionary<string, byte[]>();
        for (var index = 1; index <= 2; index++)
        {
            var name = $"000000_00_{index:00000000000000000000}.sst";
            var key = "key"u8.ToArray();
            var bytes = SstCodec.Encode(
                [
                    new SstEntry("a"u8.ToArray(), new byte[512], 1, null, false),
                    new SstEntry("b"u8.ToArray(), new byte[512], 2, null, false),
                    new SstEntry(key, [(byte)index], 7, null, false)
                ], [], PantsPerformanceGoal.Latency);
            inputs.Add(name, bytes);
            await File.WriteAllBytesAsync(Path.Combine(sstDirectory.FullName, name), bytes);
            manifest.Files.Add(new FileMeta
            {
                Name = name,
                ColumnFamilyId = 0,
                Level = 0,
                SstSequence = (ulong)index,
                SizeBytes = (ulong)bytes.Length,
                ContentCrc32C = DiskFormat.Crc32C(bytes),
                SmallestKey = [97],
                LargestKey = key.Select(static value => (int)value).ToArray(),
                SmallestSequence = 1,
                LargestSequence = 7
            });
        }

        await File.WriteAllTextAsync(Path.Combine(directory.Path, "FORMAT"), "midge-format-version=3\n");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));
        var options = PantsOpenOptions.Local(directory.Path).WithCompaction(
            new PantsCompactionConfiguration(L0FileCountTrigger: 2, TargetSstSizeBytes: 1024,
                BackgroundEnabled: false));
        var failpoints = new CountingFailpointHandler(Failpoint.AfterCompactionOutputDurable);
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(failpoints)))
        {
            await Assert.ThrowsAsync<PantsCorruptionException>(() => database.Maintenance.CompactAllAsync().AsTask());

            Assert.True(failpoints.HitCount > 0);
            var layout = await database.Diagnostics.GetStorageLayoutAsync();
            Assert.Equal(inputs.Keys.Order(), layout.Levels.SelectMany(static level => level.Files)
                .Select(static file => file.Name).Order());
            Assert.Equal(inputs.Keys.Order(), Directory.GetFiles(sstDirectory.FullName, "*.sst")
                .Select(Path.GetFileName).Order());
            foreach (var (name, bytes) in inputs)
            {
                Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(sstDirectory.FullName, name)));
            }
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var reopenedLayout = await reopened.Diagnostics.GetStorageLayoutAsync();
        Assert.Equal(inputs.Keys.Order(), reopenedLayout.Levels.SelectMany(static level => level.Files)
            .Select(static file => file.Name).Order());
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal(new byte[512], (await reader.GetAsync("a"u8.ToArray()))?.ToArray());
    }
}
