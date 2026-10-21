using System.Text.Json;
using Cntryl.Pants.Runtime.Internal;
using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Storage.Internal.Compaction;
using Cntryl.Pants.Storage.Internal.Compaction.Compaction;
using Cntryl.Pants.Storage.Internal.Manifest;
using Cntryl.Pants.Storage.Internal.Sst;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsFragmentationParityTests
{
    static readonly string[] PlanTargetKeys = ["a1", "b1", "c1", "d1", "e1", "f1"];

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public async Task ShouldRejectFragmentOutputOutsideCapturedSourceBoundsBeforePublication()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory.Path, FragmentSourceShape.BoundsTooNarrow);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            fixture.Options,
            RuntimeDependencies.Default);

        await Assert.ThrowsAsync<PantsFencedException>(() => database.Maintenance.CompactAllAsync().AsTask());

        AssertAuthoritativeInputsRemain(fixture);
        AssertNoCompactionIntent(fixture.RootPath);
    }

    [Fact]
    public async Task ShouldRejectFragmentOutputThatDoesNotPreserveTheCapturedCut()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory.Path, FragmentSourceShape.OnlyBeforeCut);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            fixture.Options,
            RuntimeDependencies.Default);

        await Assert.ThrowsAsync<PantsFencedException>(() => database.Maintenance.CompactAllAsync().AsTask());

        AssertAuthoritativeInputsRemain(fixture);
        AssertNoCompactionIntent(fixture.RootPath);
    }

    [Fact]
    public async Task ShouldPublishValidFragmentWhenCriticalL0WorkArrivesDuringItsBuild()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory.Path, FragmentSourceShape.Valid);
        var failpoint = new BlockingCompactionOutputFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            fixture.Options,
            new RuntimeDependencies(failpoint));
        var compaction = database.Maintenance.CompactAllAsync().AsTask();
        try
        {
            await failpoint.WaitUntilBlockedAsync(TestTimeouts.Expected);

            var inFlightOutputs = Directory.EnumerateFiles(
                    Path.Combine(fixture.RootPath, "sst"),
                    "*.tmp",
                    SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .Where(name => name is not null &&
                               name.Contains(".compaction.000000_01_", StringComparison.Ordinal))
                .ToArray();
            Assert.Single(inFlightOutputs);
            Assert.Contains(".compaction.000000_01_", inFlightOutputs[0], StringComparison.Ordinal);

            await PutAndFlushAsync(database, "z0", "new-l0-0");
            await PutAndFlushAsync(database, "z1", "new-l0-1");
            Assert.Equal(
                2,
                (await database.Diagnostics.GetStorageLayoutAsync()).Levels
                    .Single(static level => level.Level == 0)
                    .FileCount);
        }
        finally
        {
            failpoint.Release();
        }

        await compaction.WaitAsync(TestTimeouts.Expected);
        var finalLayout = await database.Diagnostics.GetStorageLayoutAsync();
        Assert.DoesNotContain(finalLayout.Levels, static level => level.Level == 0);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("current", TestBytes.ToText((await reader.GetAsync("c0"u8.ToArray()))!.Value));
        Assert.Equal("new-l0-0", TestBytes.ToText((await reader.GetAsync("z0"u8.ToArray()))!.Value));
        Assert.Equal("new-l0-1", TestBytes.ToText((await reader.GetAsync("z1"u8.ToArray()))!.Value));
    }

    [Fact]
    public void ShouldRejectFragmentPlanWhenCapturedSourceIdentityChanges()
    {
        var (manifest, plan) = CreateFragmentPlan();
        var work = new CompactionWorkSnapshot(
            ManifestReadSnapshot.Create(manifest),
            [plan],
            []);
        var changedSource = manifest.Files[0].Clone();
        changedSource.ContentCrc32C = checked(changedSource.ContentCrc32C!.Value + 1);
        var current = ManifestReadSnapshot.Create(new ManifestState
        {
            Files = [changedSource, .. manifest.Files.Skip(1)]
        });

        Assert.False(LocalDiskStore.IsCompactionWorkSnapshotCurrent(work, current));
    }

    [Fact]
    public void ShouldRejectFragmentPlanWhenSourceNeighborTopologyChanges()
    {
        var (manifest, plan) = CreateFragmentPlan();
        var work = new CompactionWorkSnapshot(
            ManifestReadSnapshot.Create(manifest),
            [plan],
            []);
        var neighbor = new FileMeta
        {
            Name = "000000_01_00000000000000000020.sst",
            ColumnFamilyId = 0,
            Level = 1,
            SstSequence = 20,
            SizeBytes = 128,
            ContentCrc32C = 20,
            SmallestKey = TestBytes.FromString("c0"),
            LargestKey = TestBytes.FromString("d0"),
            KeyBoundsComplete = true
        };
        var current = ManifestReadSnapshot.Create(new ManifestState
        {
            Files = [.. manifest.Files, neighbor]
        });

        Assert.False(LocalDiskStore.IsCompactionWorkSnapshotCurrent(work, current));
    }

    [Fact]
    public async Task ShouldRecoverPublishedFragmentAfterManifestBatchWithRawHistoryIntact()
    {
        using var directory = new TemporaryDirectory();
        using var recoveryDirectory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory.Path, FragmentSourceShape.Valid);
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.AfterCompactionManifestPublish,
            throwOnHit: true);
        string[] outputNames;
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         fixture.Options,
                         new RuntimeDependencies(failpoint)))
        {
            await Assert.ThrowsAnyAsync<PantsException>(() =>
                database.Maintenance.CompactAllAsync().AsTask());

            using var intent = JsonDocument.Parse(
                await File.ReadAllBytesAsync(Path.Combine(fixture.RootPath, "intent_log.json")));
            var publication = Assert.Single(intent.RootElement.EnumerateArray())
                .GetProperty("CompactionPublish");
            Assert.Equal("OutputDurable", publication.GetProperty("phase").GetString());
            Assert.Equal(
                fixture.SourceName,
                Assert.Single(publication.GetProperty("removed").EnumerateArray()).GetString());
            outputNames = publication.GetProperty("added").EnumerateArray()
                .Select(static output => output.GetProperty("name").GetString()!)
                .ToArray();
            Assert.Equal(2, outputNames.Length);

            var layoutNames = (await database.Diagnostics.GetStorageLayoutAsync()).Levels
                .SelectMany(static level => level.Files)
                .Select(static file => file.Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain(fixture.SourceName, layoutNames);
            Assert.All(fixture.TargetNames, name => Assert.Contains(name, layoutNames));

            CopyCrashImage(fixture.RootPath, recoveryDirectory.Path);
            ExpireWriterLease(recoveryDirectory.Path);
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            CreateOptions(recoveryDirectory.Path));
        var recoveredMetrics = await reopened.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(PantsEngineHealth.Healthy, recoveredMetrics.Health);
        using var recoveredIntent = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(recoveryDirectory.Path, "intent_log.json")));
        Assert.Empty(recoveredIntent.RootElement.EnumerateArray());

        var recoveredRows = outputNames
            .SelectMany(name => SstCodec.Decode(File.ReadAllBytes(
                Path.Combine(recoveryDirectory.Path, "sst", name))).Entries)
            .Select(Describe)
            .ToArray();
        Assert.Equal(fixture.SourceEntries.Select(Describe), recoveredRows);
        foreach (var (name, expectedBytes) in fixture.TargetBytesByName)
        {
            Assert.Equal(
                expectedBytes,
                await File.ReadAllBytesAsync(Path.Combine(recoveryDirectory.Path, "sst", name)));
        }

        var recoveredRanges = outputNames
            .SelectMany(name => SstCodec.Decode(File.ReadAllBytes(
                Path.Combine(recoveryDirectory.Path, "sst", name))).RangeTombstones)
            .OrderBy(static range => range.Start, ByteArrayComparer.Instance)
            .Select(static range => (
                Start: TestBytes.ToText(range.Start),
                End: TestBytes.ToText(range.End),
                range.Sequence))
            .ToArray();
        Assert.Equal(
            [(Start: "b0", End: "d1", Sequence: 4UL), (Start: "d1", End: "e0", Sequence: 4UL)],
            recoveredRanges);

        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("current", TestBytes.ToText((await reader.GetAsync("c0"u8.ToArray()))!.Value));
        Assert.Null(await reader.GetAsync("d0"u8.ToArray()));
        Assert.Equal("right", TestBytes.ToText((await reader.GetAsync("f9"u8.ToArray()))!.Value));
    }

    static async Task<FragmentFixture> CreateFixtureAsync(string rootPath, FragmentSourceShape shape)
    {
        var sstDirectory = Directory.CreateDirectory(Path.Combine(rootPath, "sst"));
        var sourceName = SstName(1, 1);
        SstEntry[] sourceEntries = shape switch
        {
            FragmentSourceShape.OnlyBeforeCut =>
            [
                Entry("a0", "left", 7),
                Entry("b0", "left", 6)
            ],
            _ =>
            [
                Entry("a0", "left", 7),
                Entry("b0", "masked", 6),
                Entry("c0", "current", 8),
                Entry("c0", "expired", 5, expiration: 1),
                Entry("d0", null, 8, isDelete: true),
                Entry("d0", "deleted", 4),
                Entry("e0", "right", 7),
                Entry("f9", "right", 9)
            ]
        };
        RangeTombstone[] sourceRanges = shape == FragmentSourceShape.Valid
            ? [new RangeTombstone("b0"u8.ToArray(), "e0"u8.ToArray(), 4)]
            : [];
        var sourceBytes = SstCodec.Encode(sourceEntries, sourceRanges, PantsPerformanceGoal.Latency);
        await File.WriteAllBytesAsync(Path.Combine(sstDirectory.FullName, sourceName), sourceBytes);

        var sourceSmallest = shape == FragmentSourceShape.BoundsTooNarrow ? "b0" : "a0";
        var sourceLargest = shape == FragmentSourceShape.BoundsTooNarrow ? "e0" : "f9";
        var files = new List<FileMeta>
        {
            new()
            {
                Name = sourceName,
                ColumnFamilyId = 0,
                Level = 1,
                SstSequence = 1,
                SizeBytes = checked((ulong)sourceBytes.Length),
                ContentCrc32C = DiskFormat.Crc32C(sourceBytes),
                SmallestKey = TestBytes.FromString(sourceSmallest),
                LargestKey = TestBytes.FromString(sourceLargest),
                KeyBoundsComplete = true,
                SmallestSequence = sourceEntries.Min(static entry => entry.Sequence),
                LargestSequence = sourceEntries.Max(static entry => entry.Sequence)
            }
        };
        var targetNames = new List<string>();
        var targetBytesByName = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var targetKeys = new[] { "a1", "b1", "c1", "d1", "e1", "f1" };
        for (var index = 0; index < targetKeys.Length; index++)
        {
            var sequence = (ulong)index + 2;
            var name = SstName(2, sequence);
            var key = TestBytes.FromString(targetKeys[index]);
            var payload = new byte[32 * 1024];
            new Random(index + 42).NextBytes(payload);
            var bytes = SstCodec.Encode(
                [new SstEntry(key, payload, 100 + sequence, null, false)],
                [],
                PantsPerformanceGoal.Latency);
            await File.WriteAllBytesAsync(Path.Combine(sstDirectory.FullName, name), bytes);
            files.Add(new FileMeta
            {
                Name = name,
                ColumnFamilyId = 0,
                Level = 2,
                SstSequence = sequence,
                SizeBytes = checked((ulong)bytes.Length),
                ContentCrc32C = DiskFormat.Crc32C(bytes),
                SmallestKey = key,
                LargestKey = key,
                KeyBoundsComplete = true,
                SmallestSequence = 100 + sequence,
                LargestSequence = 100 + sequence
            });
            targetNames.Add(name);
            targetBytesByName.Add(name, bytes);
        }

        var manifest = new ManifestState
        {
            LastPersistedSequence = 200,
            NextSstSeqs = new Dictionary<uint, ulong> { [0] = 8 },
            ColumnFamilies = [new ColumnFamilyMeta { Id = 0, Name = "default" }],
            Files = files
        };
        await File.WriteAllTextAsync(Path.Combine(rootPath, "FORMAT"), "midge-format-version=3\n");
        await File.WriteAllTextAsync(
            Path.Combine(rootPath, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));

        return new FragmentFixture(
            rootPath,
            CreateOptions(rootPath),
            sourceName,
            targetNames.ToArray(),
            sourceBytes,
            sourceEntries,
            sourceRanges,
            targetBytesByName);
    }

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.Local(path)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: 2,
                L1TargetSizeBytes: 1,
                MaximumLevels: 3,
                TargetSstSizeBytes: 4096,
                BackgroundEnabled: false));

    static (ManifestState Manifest, CompactionPlan Plan) CreateFragmentPlan()
    {
        var source = new FileMeta
        {
            Name = "000000_01_00000000000000000001.sst",
            ColumnFamilyId = 0,
            Level = 1,
            SstSequence = 1,
            SizeBytes = 128,
            ContentCrc32C = 1,
            SmallestKey = TestBytes.FromString("a0"),
            LargestKey = TestBytes.FromString("f9"),
            KeyBoundsComplete = true
        };
        var targets = PlanTargetKeys
            .Select((key, index) => new FileMeta
            {
                Name = $"000000_02_{index + 2:00000000000000000000}.sst",
                ColumnFamilyId = 0,
                Level = 2,
                SstSequence = checked((ulong)index + 2),
                SizeBytes = 32 * 1024,
                ContentCrc32C = checked((uint)index + 2),
                SmallestKey = TestBytes.FromString(key),
                LargestKey = TestBytes.FromString(key),
                KeyBoundsComplete = true
            })
            .ToArray();
        var manifest = new ManifestState { Files = [source, .. targets] };
        var plan = new CompactionPlan(
            1,
            1,
            0,
            null,
            false,
            false,
            [source],
            CompactionPurpose.Fragment,
            TestBytes.FromString("d1"));
        return (manifest, plan);
    }

    static SstEntry Entry(
        string key,
        string? value,
        ulong sequence,
        ulong? expiration = null,
        bool isDelete = false) =>
        new(
            TestBytes.FromString(key),
            value is null ? null : TestBytes.FromString(value),
            sequence,
            expiration,
            isDelete);

    static async Task PutAndFlushAsync(IPantsDatabase database, string key, string value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }

    static string SstName(uint level, ulong sequence) =>
        $"000000_{level:00}_{sequence:00000000000000000000}.sst";

    static (string Key, string? Value, ulong Sequence, ulong? Expiration, bool IsDelete) Describe(
        SstEntry entry) =>
        (
            TestBytes.ToText(entry.Key),
            entry.Value is null ? null : TestBytes.ToText(entry.Value),
            entry.Sequence,
            entry.Expiration,
            entry.IsDelete);

    static void AssertAuthoritativeInputsRemain(FragmentFixture fixture)
    {
        Assert.Equal(fixture.SourceBytes, File.ReadAllBytes(Path.Combine(
            fixture.RootPath,
            "sst",
            fixture.SourceName)));
        var names = Directory.EnumerateFiles(
                Path.Combine(fixture.RootPath, "sst"),
                "*.sst",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { fixture.SourceName }.Concat(fixture.TargetNames).Order(StringComparer.Ordinal),
            names);
        foreach (var (name, expectedBytes) in fixture.TargetBytesByName)
        {
            Assert.Equal(
                expectedBytes,
                File.ReadAllBytes(Path.Combine(fixture.RootPath, "sst", name)));
        }
    }

    static void AssertNoCompactionIntent(string rootPath)
    {
        var path = Path.Combine(rootPath, "intent_log.json");
        if (!File.Exists(path))
        {
            return;
        }

        using var intent = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Empty(intent.RootElement.EnumerateArray());
    }

    static void CopyCrashImage(string source, string destination)
    {
        foreach (var sourceDirectory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, sourceDirectory)));
        }

        foreach (var sourceFile in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source, sourceFile);
            if (relativePath is "LOCK" or ".midge_leader.lock")
            {
                continue;
            }

            var destinationFile = Path.Combine(destination, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, true);
        }
    }

    static void ExpireWriterLease(string path)
    {
        var leasePath = Path.Combine(path, ".midge_leader");
        var lines = File.ReadAllLines(leasePath)
            .Where(static line => !line.StartsWith("checksum: ", StringComparison.Ordinal))
            .Select(line => line.StartsWith("acquired_at: ", StringComparison.Ordinal)
                ? "acquired_at: 1970-01-01T00:00:00Z"
                : line)
            .ToArray();
        File.WriteAllLines(leasePath, lines);
    }

    enum FragmentSourceShape
    {
        Valid,
        BoundsTooNarrow,
        OnlyBeforeCut
    }

    sealed record FragmentFixture(
        string RootPath,
        PantsOpenOptions Options,
        string SourceName,
        string[] TargetNames,
        byte[] SourceBytes,
        SstEntry[] SourceEntries,
        RangeTombstone[] SourceRanges,
        IReadOnlyDictionary<string, byte[]> TargetBytesByName);
}
