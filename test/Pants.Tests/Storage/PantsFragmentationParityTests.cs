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
    public async Task ShouldKeepFinalKeyAndEndingRangeAdjacentToExistingSourceLevelNeighbor()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(
            directory.Path,
            FragmentSourceShape.FinalKeyWithEndingRangeNeighbor);
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.AfterCompactionManifestPublish,
            throwOnHit: true);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            fixture.Options,
            new RuntimeDependencies(failpoint));

        await Assert.ThrowsAnyAsync<PantsException>(() => database.Maintenance.CompactAllAsync().AsTask());

        var levelFiles = (await database.Diagnostics.GetStorageLayoutAsync()).Levels
            .Single(static level => level.Level == 1)
            .Files
            .OrderBy(static file => TestBytes.ToText(file.SmallestKey!.Value), StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(3, levelFiles.Length);
        Assert.DoesNotContain(levelFiles, file => file.Name == fixture.SourceName);
        Assert.Contains(levelFiles, file => file.Name == fixture.NeighborName);

        var outputs = levelFiles
            .Where(file => file.Name != fixture.NeighborName)
            .Select(file => SstCodec.Decode(File.ReadAllBytes(Path.Combine(
                fixture.RootPath,
                "sst",
                file.Name))))
            .ToArray();
        Assert.Equal(2, outputs.Length);
        Assert.Equal(
            fixture.SourceEntries.Select(Describe),
            outputs.SelectMany(static output => output.Entries).Select(Describe));
        Assert.Equal(
            [(Start: "a", End: "d1", Sequence: 4UL), (Start: "d1", End: "z", Sequence: 4UL)],
            outputs.SelectMany(static output => output.RangeTombstones)
                .OrderBy(static range => range.Start, ByteArrayComparer.Instance)
                .Select(static range => (
                    Start: TestBytes.ToText(range.Start),
                    End: TestBytes.ToText(range.End),
                    range.Sequence)));
        Assert.Contains(outputs[1].Entries, static entry => TestBytes.ToText(entry.Key) == "z");
        Assert.Contains(outputs[1].RangeTombstones, static range => TestBytes.ToText(range.End) == "z");

        Assert.True(ByteArrayComparer.Instance.Compare(
            levelFiles[0].LargestKey!.Value.ToArray(),
            levelFiles[1].SmallestKey!.Value.ToArray()) <= 0);
        Assert.True(ByteArrayComparer.Instance.Compare(
            levelFiles[1].LargestKey!.Value.ToArray(),
            levelFiles[2].SmallestKey!.Value.ToArray()) <= 0);
    }

    [Fact]
    public async Task ShouldPublishConsecutiveOutputFilesWhenSoftRollPrecedesEmptyFragmentCut()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory.Path, FragmentSourceShape.SoftRollBeforeEmptyCut);
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.AfterCompactionManifestPublish,
            throwOnHit: true);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            fixture.Options,
            new RuntimeDependencies(failpoint));

        await Assert.ThrowsAnyAsync<PantsException>(() => database.Maintenance.CompactAllAsync().AsTask());

        using var intent = IntentLogCodec.ToDocument(IntentLogCodec.Read(
            await File.ReadAllBytesAsync(Path.Combine(fixture.RootPath, "intent_log.json"))).Entries);
        var publication = Assert.Single(intent.RootElement.EnumerateArray())
            .GetProperty("CompactionPublish");
        var outputNames = publication.GetProperty("added").EnumerateArray()
            .Select(static output => output.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Equal([8UL, 9UL], outputNames.Select(ParseSstSequence));

        var outputs = outputNames
            .Select(name => SstCodec.Decode(File.ReadAllBytes(Path.Combine(
                fixture.RootPath,
                "sst",
                name))))
            .ToArray();
        Assert.Equal(2, outputs.Length);
        Assert.Empty(outputs[0].Entries);
        Assert.Equal([("a", "b", 4UL)], outputs[0].RangeTombstones.Select(static range => (
            TestBytes.ToText(range.Start),
            TestBytes.ToText(range.End),
            range.Sequence)));
        Assert.Equal(["z"], outputs[1].Entries.Select(static entry => TestBytes.ToText(entry.Key)));
        Assert.Empty(outputs[1].RangeTombstones);
        Assert.Equal(
            fixture.SourceEntries.Select(Describe),
            outputs.SelectMany(static output => output.Entries).Select(Describe));
    }

    [Fact]
    public async Task ShouldRetainSourceWhenFragmentSplitBoundaryExceedsCompactionBudget()
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(
            directory.Path,
            FragmentSourceShape.OversizedSplitBoundary);
        var outputFailpoint = new CountingCompactionOutputDurableFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            fixture.Options,
            new RuntimeDependencies(outputFailpoint));

        await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
            database.Maintenance.CompactAllAsync().AsTask());

        Assert.Equal(0, outputFailpoint.HitCount);
        AssertAuthoritativeInputsRemain(fixture);
        AssertNoCompactionIntent(fixture.RootPath);
        Assert.Equal(PantsEngineHealth.Healthy, (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);
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

            using var intent = IntentLogCodec.ToDocument(IntentLogCodec.Read(
                await File.ReadAllBytesAsync(Path.Combine(fixture.RootPath, "intent_log.json"))).Entries);
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
        using var recoveredIntent = IntentLogCodec.ToDocument(IntentLogCodec.Read(
            await File.ReadAllBytesAsync(Path.Combine(recoveryDirectory.Path, "intent_log.json"))).Entries);
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ShouldCleanupUnpublishedFragmentsWhenStagingFails(int failedOutput)
    {
        using var directory = new TemporaryDirectory();
        var fixture = await CreateFixtureAsync(directory.Path, FragmentSourceShape.Valid);
        var failpoint = new ThrowOnNthCompactionOutputFailpointHandler(failedOutput);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            fixture.Options,
            new RuntimeDependencies(failpoint));

        await Assert.ThrowsAnyAsync<PantsException>(() => database.Maintenance.CompactAllAsync().AsTask());

        Assert.Equal(failedOutput, failpoint.HitCount);
        AssertAuthoritativeInputsRemain(fixture);
        AssertNoCompactionIntent(fixture.RootPath);
        var stagingDirectory = Path.Combine(fixture.RootPath, "sst", ".flush-staging");
        if (Directory.Exists(stagingDirectory))
        {
            Assert.Empty(Directory.EnumerateFiles(
                stagingDirectory,
                "*.compaction.*.tmp",
                SearchOption.AllDirectories));
        }
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
            FragmentSourceShape.FinalKeyWithEndingRangeNeighbor =>
            [
                Entry("a", "left", 7),
                Entry("n", "middle", 8),
                Entry("z", "last", 9)
            ],
            FragmentSourceShape.SoftRollBeforeEmptyCut =>
            [
                Entry("z", "right", 9)
            ],
            FragmentSourceShape.OversizedSplitBoundary =>
            [
                Entry("a", "left", 7),
                Entry("z", "right", 9)
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
        RangeTombstone[] sourceRanges = shape switch
        {
            FragmentSourceShape.Valid => [new RangeTombstone("b0"u8.ToArray(), "e0"u8.ToArray(), 4)],
            FragmentSourceShape.FinalKeyWithEndingRangeNeighbor =>
                [new RangeTombstone("a"u8.ToArray(), "z"u8.ToArray(), 4)],
            FragmentSourceShape.SoftRollBeforeEmptyCut =>
                [new RangeTombstone("a"u8.ToArray(), "b"u8.ToArray(), 4)],
            _ => []
        };
        var sourceBytes = SstCodec.Encode(sourceEntries, sourceRanges, PantsPerformanceGoal.Latency);
        await File.WriteAllBytesAsync(Path.Combine(sstDirectory.FullName, sourceName), sourceBytes);

        var sourceSmallest = shape switch
        {
            FragmentSourceShape.BoundsTooNarrow => "b0",
            FragmentSourceShape.FinalKeyWithEndingRangeNeighbor or
                FragmentSourceShape.SoftRollBeforeEmptyCut or
                FragmentSourceShape.OversizedSplitBoundary => "a",
            _ => "a0"
        };
        var sourceLargest = shape switch
        {
            FragmentSourceShape.BoundsTooNarrow => "e0",
            FragmentSourceShape.FinalKeyWithEndingRangeNeighbor or
                FragmentSourceShape.SoftRollBeforeEmptyCut or
                FragmentSourceShape.OversizedSplitBoundary => "z",
            _ => "f9"
        };
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
        string? neighborName = null;
        byte[]? neighborBytes = null;
        if (shape == FragmentSourceShape.FinalKeyWithEndingRangeNeighbor)
        {
            neighborName = SstName(1, 20);
            var neighborEntries = new[]
            {
                Entry("z", "neighbor", 20),
                Entry("zz", "neighbor", 21)
            };
            neighborBytes = SstCodec.Encode(neighborEntries, [], PantsPerformanceGoal.Latency);
            await File.WriteAllBytesAsync(Path.Combine(sstDirectory.FullName, neighborName), neighborBytes);
            files.Add(new FileMeta
            {
                Name = neighborName,
                ColumnFamilyId = 0,
                Level = 1,
                SstSequence = 20,
                SizeBytes = checked((ulong)neighborBytes.Length),
                ContentCrc32C = DiskFormat.Crc32C(neighborBytes),
                SmallestKey = TestBytes.FromString("z"),
                LargestKey = TestBytes.FromString("zz"),
                KeyBoundsComplete = true,
                SmallestSequence = 20,
                LargestSequence = 21
            });
        }
        var targetNames = new List<string>();
        var targetBytesByName = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var targetKeys = shape == FragmentSourceShape.OversizedSplitBoundary
            ? [new string('b', 2 * 1024 * 1024)]
            : PlanTargetKeys;
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
            NextSstSeqs = new Dictionary<uint, ulong>
            {
                [0] = shape == FragmentSourceShape.FinalKeyWithEndingRangeNeighbor ? 21UL : 8UL
            },
            FlushedThrough = new Dictionary<uint, ulong> { [0] = 200 },
            Files = files
        };
        await File.WriteAllTextAsync(Path.Combine(rootPath, "FORMAT"), "midge-format-version=9\n");
        await File.WriteAllBytesAsync(
            Path.Combine(rootPath, "manifest.snapshot.json"),
            ManifestCodec.Encode(manifest, JsonOptions));

        return new FragmentFixture(
            rootPath,
            CreateOptions(
                rootPath,
                shape == FragmentSourceShape.FinalKeyWithEndingRangeNeighbor ? 1 : 2,
                shape == FragmentSourceShape.OversizedSplitBoundary),
            sourceName,
            neighborName,
            neighborBytes,
            targetNames.ToArray(),
            sourceBytes,
            sourceEntries,
            sourceRanges,
            targetBytesByName);
    }

    static PantsOpenOptions CreateOptions(
        string path,
        int l0FileCountTrigger = 2,
        bool constrainedCompactionBudget = false)
    {
        var options = PantsOpenOptions.Local(path)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: l0FileCountTrigger,
                L1TargetSizeBytes: 1,
                MaximumLevels: 3,
                TargetSstSizeBytes: 4096,
                BackgroundEnabled: false));
        return constrainedCompactionBudget
            ? options.WithMemoryBudget(PantsMemoryBudget.FromBytes(8L * 1024 * 1024))
                .WithTransactionMemoryPool(2L * 1024 * 1024)
            : options;
    }

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

    static ulong ParseSstSequence(string name) => ulong.Parse(
        name[(name.LastIndexOf('_') + 1)..^4],
        System.Globalization.CultureInfo.InvariantCulture);

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
            new[] { fixture.SourceName }
                .Concat(fixture.NeighborName is null ? [] : [fixture.NeighborName])
                .Concat(fixture.TargetNames)
                .Order(StringComparer.Ordinal),
            names);
        if (fixture.NeighborName is not null)
        {
            Assert.Equal(
                fixture.NeighborBytes,
                File.ReadAllBytes(Path.Combine(fixture.RootPath, "sst", fixture.NeighborName)));
        }

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

        using var intent = IntentLogCodec.ToDocument(IntentLogCodec.Read(File.ReadAllBytes(path)).Entries);
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
        OnlyBeforeCut,
        FinalKeyWithEndingRangeNeighbor,
        SoftRollBeforeEmptyCut,
        OversizedSplitBoundary
    }

    sealed class CountingCompactionOutputDurableFailpointHandler : IFailpointHandler
    {
        int _hitCount;

        public int HitCount => Volatile.Read(ref _hitCount);

        public void Hit(Failpoint failpoint)
        {
            if (failpoint == Failpoint.AfterCompactionOutputDurable)
            {
                Interlocked.Increment(ref _hitCount);
            }
        }
    }

    sealed class ThrowOnNthCompactionOutputFailpointHandler(int failAtHit) : IFailpointHandler
    {
        int _hitCount;

        public int HitCount => Volatile.Read(ref _hitCount);

        public void Hit(Failpoint failpoint)
        {
            if (failpoint == Failpoint.AfterCompactionOutputDurable &&
                Interlocked.Increment(ref _hitCount) == failAtHit)
            {
                throw new IOException($"Injected compaction output failure at hit {failAtHit}.");
            }
        }
    }

    sealed record FragmentFixture(
        string RootPath,
        PantsOpenOptions Options,
        string SourceName,
        string? NeighborName,
        byte[]? NeighborBytes,
        string[] TargetNames,
        byte[] SourceBytes,
        SstEntry[] SourceEntries,
        RangeTombstone[] SourceRanges,
        IReadOnlyDictionary<string, byte[]> TargetBytesByName);
}
