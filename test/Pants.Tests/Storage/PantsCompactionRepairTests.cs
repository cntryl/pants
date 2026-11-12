using System.Text.Json;
using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Storage.Internal.Manifest;
using Cntryl.Pants.Storage.Internal.Sst;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsCompactionRepairTests
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public async Task ShouldRepairOverlapAboveTheMergeFanInAndRecoverEveryWinningValue()
    {
        using var directory = new TemporaryDirectory();
        var sstDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "sst"));
        var files = new List<FileMeta>();
        for (var index = 0; index < 9; index++)
        {
            var sequence = checked((ulong)(index * 4 + 1));
            var entries = new[]
            {
                Entry("a", "anchor", sequence),
                Entry($"key-{index}", $"value-{index}", sequence + 1),
                Entry("m", index == 8 ? null : "old", sequence + 2, isDelete: index == 8),
                Entry("z", "edge", sequence + 3)
            };
            var name = SstName(1, checked((ulong)index + 1));
            var bytes = SstCodec.Encode(entries, [], PantsPerformanceGoal.Latency);
            await File.WriteAllBytesAsync(Path.Combine(sstDirectory.FullName, name), bytes);
            files.Add(Metadata(name, checked((ulong)index + 1), entries, bytes, "a", "z"));
        }

        var manifest = new ManifestState
        {
            LastPersistedSequence = 36,
            NextSstSeqs = new Dictionary<uint, ulong> { [0] = 10 },
            FlushedThrough = new Dictionary<uint, ulong> { [0] = 36 },
            Files = files
        };
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "FORMAT"), "midge-format-version=9\n");
        await File.WriteAllBytesAsync(
            Path.Combine(directory.Path, "manifest.snapshot.json"),
            ManifestCodec.Encode(manifest, JsonOptions));
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: 2,
                L1TargetSizeBytes: 1024 * 1024,
                MaximumLevels: 3,
                TargetSstSizeBytes: 256,
                MaximumInputFiles: 2,
                BackgroundEnabled: false));
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, RuntimeDependencies.Default))
        {
            await database.Maintenance.CompactAllAsync();
            await AssertValuesAsync(database);
            var outputs = (await database.Diagnostics.GetStorageLayoutAsync()).Levels
                .SelectMany(static level => level.Files).ToArray();
            Assert.DoesNotContain(outputs, output => files.Any(input => input.Name == output.Name));
            Assert.All(outputs, static output => Assert.Equal(1, output.Level));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(sstDirectory.FullName, ".flush-staging")));
        }

        await using var reopened = await PantsDatabase.OpenForTestingAsync(options, RuntimeDependencies.Default);
        await AssertValuesAsync(reopened);
    }

    static async Task AssertValuesAsync(IPantsDatabase database)
    {
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 9; index++)
        {
            Assert.Equal($"value-{index}", TestBytes.ToText(
                (await reader.GetAsync(TestBytes.FromString($"key-{index}")))!.Value));
        }

        Assert.Null(await reader.GetAsync("m"u8.ToArray()));
        Assert.Equal("anchor", TestBytes.ToText((await reader.GetAsync("a"u8.ToArray()))!.Value));
    }

    [Fact]
    public async Task ShouldRepairUnderfullOverlapAndPreserveWinningPointTombstone()
    {
        using var directory = new TemporaryDirectory();
        var rootPath = directory.Path;
        var sstDirectory = Directory.CreateDirectory(Path.Combine(rootPath, "sst"));
        var leftName = SstName(1, 1);
        var rightName = SstName(1, 2);
        var leftEntries = new[]
        {
            Entry("a", "left", 1),
            Entry("c", "old", 2),
            Entry("d", "before-delete", 3),
            Entry("e", "left-edge", 4)
        };
        var rightEntries = new[]
        {
            Entry("b", "right", 5),
            Entry("c", "new", 6),
            Entry("d", null, 7, isDelete: true),
            Entry("f", "right-edge", 8)
        };
        var leftBytes = SstCodec.Encode(leftEntries, [], PantsPerformanceGoal.Latency);
        var rightBytes = SstCodec.Encode(rightEntries, [], PantsPerformanceGoal.Latency);
        await File.WriteAllBytesAsync(Path.Combine(sstDirectory.FullName, leftName), leftBytes);
        await File.WriteAllBytesAsync(Path.Combine(sstDirectory.FullName, rightName), rightBytes);

        var manifest = new ManifestState
        {
            LastPersistedSequence = 8,
            NextSstSeqs = new Dictionary<uint, ulong> { [0] = 3 },
            FlushedThrough = new Dictionary<uint, ulong> { [0] = 8 },
            Files =
            [
                Metadata(leftName, 1, leftEntries, leftBytes, "a", "e"),
                Metadata(rightName, 2, rightEntries, rightBytes, "b", "f")
            ]
        };
        await File.WriteAllTextAsync(Path.Combine(rootPath, "FORMAT"), "midge-format-version=9\n");
        await File.WriteAllBytesAsync(
            Path.Combine(rootPath, "manifest.snapshot.json"),
            ManifestCodec.Encode(manifest, JsonOptions));

        var options = PantsOpenOptions.Local(rootPath)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: 2,
                L1TargetSizeBytes: 1024 * 1024,
                MaximumLevels: 3,
                TargetSstSizeBytes: 1024 * 1024,
                BackgroundEnabled: false));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            RuntimeDependencies.Default);

        await database.Maintenance.CompactAllAsync();

        var level = Assert.Single((await database.Diagnostics.GetStorageLayoutAsync()).Levels);
        Assert.Equal(1, level.Level);
        var output = Assert.Single(level.Files);
        Assert.DoesNotContain(output.Name, new[] { leftName, rightName });
        var contents = SstCodec.Decode(File.ReadAllBytes(Path.Combine(rootPath, "sst", output.Name)));
        var deletion = Assert.Single(contents.Entries, static entry => TestBytes.ToText(entry.Key) == "d");
        Assert.True(deletion.IsDelete);
        Assert.Equal(7UL, deletion.Sequence);

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("left", TestBytes.ToText((await reader.GetAsync("a"u8.ToArray()))!.Value));
        Assert.Equal("new", TestBytes.ToText((await reader.GetAsync("c"u8.ToArray()))!.Value));
        Assert.Null(await reader.GetAsync("d"u8.ToArray()));
        Assert.Equal("right-edge", TestBytes.ToText((await reader.GetAsync("f"u8.ToArray()))!.Value));
    }

    static FileMeta Metadata(
        string name,
        ulong sstSequence,
        IReadOnlyList<SstEntry> entries,
        byte[] bytes,
        string smallest,
        string largest) => new()
        {
            Name = name,
            ColumnFamilyId = 0,
            Level = 1,
            SstSequence = sstSequence,
            SizeBytes = checked((ulong)bytes.Length),
            ContentCrc32C = DiskFormat.Crc32C(bytes),
            SmallestKey = TestBytes.FromString(smallest),
            LargestKey = TestBytes.FromString(largest),
            KeyBoundsComplete = true,
            SmallestSequence = entries.Min(static entry => entry.Sequence),
            LargestSequence = entries.Max(static entry => entry.Sequence)
        };

    static SstEntry Entry(string key, string? value, ulong sequence, bool isDelete = false) => new(
        TestBytes.FromString(key),
        value is null ? null : TestBytes.FromString(value),
        sequence,
        null,
        isDelete);

    static string SstName(uint level, ulong sequence) =>
        $"000000_{level:00}_{sequence:00000000000000000000}.sst";
}
