using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Pants.Tests;

public sealed class CloudMirrorSnapshotConsistencyTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public async Task ShouldNotPublishManifestGivenReferencedSstWasAddedAfterProviderSnapshot()
    {
        using var cache = new TemporaryDirectory();
        var first = CreateSst(1, "a"u8.ToArray());
        var second = CreateSst(2, "b"u8.ToArray());
        WriteLocalFixture(cache.Path, [first]);

        var walStore = new SnapshotConsistencyCloudObjectStore();
        var sstStore = new SnapshotConsistencyCloudObjectStore();
        var controlStore = new SnapshotConsistencyCloudObjectStore();
        var firstManifest = ReadLocalMetadata(cache.Path, "manifest.snapshot.json");
        sstStore.Seed(PantsCloudObjectLayout.SstPrefix + first.Metadata.Name, first.Bytes);
        controlStore.Seed(
            PantsCloudObjectLayout.MetadataPrefix + "manifest.snapshot.json",
            firstManifest);
        controlStore.Seed(
            PantsCloudObjectLayout.MetadataPrefix + "manifest.json",
            firstManifest);

        var leaseStore = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var lease = new CloudLeaseCoordinator(
            leaseStore,
            clock,
            "writer",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        _ = await lease.AcquireAsync(CancellationToken.None);
        var persistence = new ProviderCloudPersistence(
            cache.Path,
            walStore,
            sstStore,
            controlStore,
            lease);
        var snapshotRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshotRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        controlStore.BeforeGetAsync = async (objectKey, count, cancellationToken) =>
        {
            if (objectKey.EndsWith("/manifest.snapshot.json", StringComparison.Ordinal) &&
                count == 2)
            {
                snapshotRead.TrySetResult();
                await releaseSnapshotRead.Task.WaitAsync(cancellationToken);
            }
        };

        var mirror = persistence.MirrorMetadataAndSstsAsync(CancellationToken.None).AsTask();
        await snapshotRead.Task.WaitAsync(AssertionTimeout);
        AddSstAndAdvanceManifest(cache.Path, first, second);
        releaseSnapshotRead.TrySetResult();

        var failure = await Record.ExceptionAsync(() => mirror);
        AssertProviderManifestDependencies(controlStore, sstStore);
        Assert.Null(failure);
    }

    [Fact]
    public async Task ShouldNotPublishManifestGivenReferencedSstWasAddedAfterSimulatedSnapshot()
    {
        using var cache = new TemporaryDirectory();
        var first = CreateSst(1, "a"u8.ToArray());
        var second = CreateSst(2, "b"u8.ToArray());
        var unrelated = CreateSst(3, "c"u8.ToArray());
        WriteLocalFixture(cache.Path, [first]);
        using var failpoints = new ArmableBlockingCloudUploadFailpointHandler();
        var persistence = new SimulatedCloudPersistence(cache.Path, 1, failpoints);
        WriteSst(cache.Path, unrelated);
        failpoints.Arm();

        var mirror = Task.Run(persistence.MirrorMetadataAndSsts);
        await failpoints.WaitUntilEnteredAsync(AssertionTimeout);
        AddSstAndAdvanceManifest(cache.Path, first, second);
        failpoints.Release();

        var failure = await Record.ExceptionAsync(() => mirror);
        AssertSimulatedManifestDependencies(cache.Path);
        Assert.Null(failure);
    }

    static (MidgeFileMeta Metadata, byte[] Bytes) CreateSst(ulong sequence, byte[] key)
    {
        var bytes = MidgeSstCodec.Encode(
            [new MidgeSstEntry(key, "value"u8.ToArray(), sequence, null, false)],
            [],
            PantsPerformanceGoal.Latency);
        var name = $"000000_00_{sequence:00000000000000000000}.sst";
        return (
            new MidgeFileMeta
            {
                Name = name,
                Level = 0,
                SizeBytes = checked((ulong)bytes.Length),
                ContentCrc32C = MidgeDiskFormat.Crc32C(bytes),
                ColumnFamilyId = 0,
                SstSequence = sequence,
                SmallestKey = key.Select(static value => (int)value).ToArray(),
                LargestKey = key.Select(static value => (int)value).ToArray(),
                SmallestSequence = sequence,
                LargestSequence = sequence
            },
            bytes);
    }

    static void WriteLocalFixture(
        string root,
        IReadOnlyList<(MidgeFileMeta Metadata, byte[] Bytes)> ssts)
    {
        Directory.CreateDirectory(Path.Combine(root, "sst"));
        File.WriteAllText(Path.Combine(root, "FORMAT"), "midge-format-version=3\n");
        File.WriteAllBytes(Path.Combine(root, "manifest.journal"), []);
        File.WriteAllText(Path.Combine(root, "intent_log.json"), "[]");
        foreach (var sst in ssts)
        {
            WriteSst(root, sst);
        }

        WriteManifest(root, ssts.Select(static sst => sst.Metadata).ToArray());
    }

    static void AddSstAndAdvanceManifest(
        string root,
        (MidgeFileMeta Metadata, byte[] Bytes) first,
        (MidgeFileMeta Metadata, byte[] Bytes) second)
    {
        WriteSst(root, second);
        WriteManifest(root, [first.Metadata, second.Metadata]);
    }

    static void WriteSst(
        string root,
        (MidgeFileMeta Metadata, byte[] Bytes) sst) =>
        AtomicStagedFile.Write(
            Path.Combine(root, "sst", sst.Metadata.Name),
            sst.Bytes);

    static void WriteManifest(string root, MidgeFileMeta[] files)
    {
        var manifest = new MidgeManifest
        {
            LastPersistedSequence = files.Length == 0
                ? 0
                : files.Max(static file => file.LargestSequence ?? 0),
            Files = files.Select(static file => file.Clone()).ToList(),
            NextSstSeqs = new Dictionary<uint, ulong>
            {
                [0] = files.Length == 0
                    ? 1
                    : checked(files.Max(static file => file.SstSequence) + 1)
            },
            EditCheckpointId = checked((ulong)files.Length)
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        AtomicStagedFile.Write(Path.Combine(root, "manifest.snapshot.json"), bytes);
        AtomicStagedFile.Write(Path.Combine(root, "manifest.json"), bytes);
    }

    static byte[] ReadLocalMetadata(string root, string fileName) =>
        File.ReadAllBytes(Path.Combine(root, fileName));

    static void AssertProviderManifestDependencies(
        SnapshotConsistencyCloudObjectStore controlStore,
        SnapshotConsistencyCloudObjectStore sstStore)
    {
        foreach (var fileName in new[] { "manifest.snapshot.json", "manifest.json" })
        {
            var bytes = Assert.IsType<byte[]>(controlStore.GetData(
                PantsCloudObjectLayout.MetadataPrefix + fileName));
            var manifest = CloudManifestReader.DecodeManifest(bytes);
            foreach (var file in manifest.Files)
            {
                var sst = Assert.IsType<byte[]>(sstStore.GetData(
                    PantsCloudObjectLayout.SstPrefix + file.Name));
                CloudSstValidator.Validate(sst, file);
            }
        }
    }

    static void AssertSimulatedManifestDependencies(string root)
    {
        var cloudRoot = Path.Combine(root, "cloud_store");
        foreach (var fileName in new[] { "manifest.snapshot.json", "manifest.json" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(cloudRoot, "metadata", fileName));
            var manifest = CloudManifestReader.DecodeManifest(bytes);
            foreach (var file in manifest.Files)
            {
                var path = Path.Combine(cloudRoot, "sst", file.Name);
                Assert.True(File.Exists(path), $"Remote SST '{file.Name}' is missing.");
                CloudSstValidator.Validate(File.ReadAllBytes(path), file);
            }
        }
    }
}
