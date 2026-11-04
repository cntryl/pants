using System.Text.Json;
using System.Text.Json.Serialization;
using Cntryl.Pants.Cloud.Internal;
using Cntryl.Pants.Storage.Internal.Accounting;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudMirrorSnapshotConsistencyTests
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldCreditOnlyTheCapturedSharedCutWhenLaterLocalPublicationArrives(bool provider)
    {
        using var cache = new TemporaryDirectory();
        var first = CreateSst(1, "a"u8.ToArray());
        var second = CreateSst(2, "b"u8.ToArray());
        WriteLocalFixture(cache.Path, [first]);
        var owner = new MetadataAccountingOwner();
        owner.RetainPublication(Receipt(first.Metadata));
        var control = new SnapshotConsistencyCloudObjectStore();
        using var upload = new ArmableBlockingCloudUploadFailpointHandler();
        using var lease = new CloudLeaseCoordinator(new TestCloudLeaseStore(),
            new ManualClock(DateTimeOffset.UnixEpoch), "writer", TimeSpan.FromMinutes(1), TimeSpan.Zero);
        _ = await lease.AcquireAsync(CancellationToken.None);
        await using ICloudPersistence persistence = provider
            ? new ProviderCloudPersistence(cache.Path, new SnapshotConsistencyCloudObjectStore(),
                new SnapshotConsistencyCloudObjectStore(), control, lease)
            : new SimulatedCloudPersistence(cache.Path, 1, upload);
        await persistence.MirrorMetadataAndSstsAsync(CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (provider)
        {
            control.BeforeGetAsync = async (key, _, token) =>
            {
                if (key.EndsWith("/manifest.snapshot.json", StringComparison.Ordinal) && entered.TrySetResult())
                {
                    await release.Task.WaitAsync(token);
                }
            };
        }
        else
        {
            WriteSst(cache.Path, CreateSst(3, "unreferenced"u8.ToArray()));
            upload.Arm();
        }

        var mirror = Task.Run(async () => await persistence.MirrorMetadataAndSstsWithReceiptAsync(owner.Identity, CancellationToken.None));
        try
        {
            if (provider)
            {
                await entered.Task.WaitAsync(TestTimeouts.Expected);
            }
            else
            {
                await upload.WaitUntilEnteredAsync(TestTimeouts.Expected);
            }

            AddSstAndAdvanceManifest(cache.Path, first, second);
            owner.RetainPublication(Receipt(second.Metadata));
        }
        finally
        {
            release.TrySetResult();
            upload.Release();
        }

        using var publication = await mirror.WaitAsync(TestTimeouts.Expected);
        publication.InstallInto(owner);
        var sharedFiles = await CloudMetadataAuthority.ReadCommittedFilesAsync(
            provider ? control : new SimulatedCloudObjectStore(Path.Combine(cache.Path, "cloud_store")), CancellationToken.None);
        var shared = CloudManifestReader.DecodeMetadata(Assert.IsAssignableFrom<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>>(sharedFiles));
        Assert.Equal(first.Metadata.Name, Assert.Single(shared.Files).Name);
        var bucket = owner.Handle.Snapshot().Bucket(MetadataOrigin.CloudFlush, MetadataMedium.Persistent);
        Assert.Equal(1UL, bucket[MetadataCounter.FlushCommittedCount]);
        Assert.Equal(first.Metadata.SizeBytes, bucket[MetadataCounter.FlushCommittedSstBytes]);

        var otherOwner = new MetadataAccountingOwner();
        otherOwner.RetainPublication(Receipt(first.Metadata));
        publication.InstallInto(otherOwner);
        Assert.True(otherOwner.Handle.Snapshot().IncompleteObservations > 0);
        Assert.Equal(0UL, otherOwner.Handle.Snapshot().Bucket(MetadataOrigin.CloudFlush, MetadataMedium.Persistent)[MetadataCounter.FlushCommittedCount]);
        publication.Dispose();
        Assert.Throws<ObjectDisposedException>(() => publication.InstallInto(owner));

        using var laterPublication = await persistence.MirrorMetadataAndSstsWithReceiptAsync(owner.Identity, CancellationToken.None);
        laterPublication.InstallInto(owner);
        bucket = owner.Handle.Snapshot().Bucket(MetadataOrigin.CloudFlush, MetadataMedium.Persistent);
        Assert.Equal(2UL, bucket[MetadataCounter.FlushCommittedCount]);
        Assert.Equal(first.Metadata.SizeBytes + second.Metadata.SizeBytes, bucket[MetadataCounter.FlushCommittedSstBytes]);
        Assert.Equal(0UL, owner.Handle.Snapshot().IncompleteObservations);

        MetadataPublicationReceipt Receipt(FileMeta file) => new(file.Name, file.ColumnFamilyId,
            file.SstSequence, file.SizeBytes, file.ContentCrc32C,
            MetadataOrigin.CloudFlush, MetadataMedium.Persistent, owner.Timestamp());
    }

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
        await persistence.MirrorMetadataAndSstsAsync(CancellationToken.None);
        var snapshotRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshotRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        controlStore.BeforeGetAsync = async (objectKey, count, cancellationToken) =>
        {
            if (objectKey.EndsWith("/manifest.snapshot.json", StringComparison.Ordinal))
            {
                if (snapshotRead.TrySetResult())
                {
                    await releaseSnapshotRead.Task.WaitAsync(cancellationToken);
                }
            }
        };

        var mirror = persistence.MirrorMetadataAndSstsAsync(CancellationToken.None).AsTask();
        await snapshotRead.Task.WaitAsync(TestTimeouts.Expected);
        AddSstAndAdvanceManifest(cache.Path, first, second);
        releaseSnapshotRead.TrySetResult();

        var failure = await Record.ExceptionAsync(() => mirror);
        await AssertProviderManifestDependenciesAsync(controlStore, sstStore);
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
        await using var persistence = new SimulatedCloudPersistence(cache.Path, 1, failpoints);
        WriteSst(cache.Path, unrelated);
        failpoints.Arm();

        var mirror = Task.Run(persistence.MirrorMetadataAndSsts);
        await failpoints.WaitUntilEnteredAsync(TestTimeouts.Expected);
        AddSstAndAdvanceManifest(cache.Path, first, second);
        failpoints.Release();

        var failure = await Record.ExceptionAsync(() => mirror);
        AssertSimulatedManifestDependencies(cache.Path);
        Assert.Null(failure);
    }

    [Fact]
    public async Task ShouldMirrorManySimulatedCloudSstsWithinPerSstPublicationBudget()
    {
        using var cache = new TemporaryDirectory();
        const int fileCount = 24;
        const int valueBytes = 64 * 1024;
        var ssts = Enumerable.Range(1, fileCount)
            .Select(sequence => CreateSst(
                checked((ulong)sequence),
                System.Text.Encoding.UTF8.GetBytes($"key-{sequence:D2}"),
                valueBytes))
            .ToArray();
        WriteLocalFixture(cache.Path, ssts);

        var budget = new ResourceBudget(1024 * 1024);
        var aggregateSstBytes = ssts.Sum(static sst => (long)sst.Bytes.Length);
        Assert.True(aggregateSstBytes > budget.Limit);
        var admission = SstPublicationAdmission.Gated(budget, new MaintenanceMemoryGate());
        await using var persistence = new SimulatedCloudPersistence(
            cache.Path,
            1,
            sstPublicationAdmission: admission);

        Assert.InRange(budget.Peak, 1, budget.Limit);
        Assert.True(budget.Peak < aggregateSstBytes);
        Assert.Equal(0, budget.Current);
        Assert.Equal(
            fileCount,
            Directory.GetFiles(Path.Combine(cache.Path, "cloud_store", "sst"), "*.sst").Length);
        AssertSimulatedManifestDependencies(cache.Path);
    }

    [Fact]
    public async Task ShouldHonorCancellationWhileWaitingForSstMirrorAdmission()
    {
        using var cache = new TemporaryDirectory();
        var first = CreateSst(1, "a"u8.ToArray());
        var second = CreateSst(2, "b"u8.ToArray());
        WriteLocalFixture(cache.Path, [first]);

        var budget = new ResourceBudget(1024 * 1024);
        var gate = new MaintenanceMemoryGate();
        var admission = SstPublicationAdmission.Gated(budget, gate);
        await using var persistence = new SimulatedCloudPersistence(
            cache.Path,
            1,
            sstPublicationAdmission: admission);
        WriteSst(cache.Path, second);
        WriteManifest(cache.Path, [first.Metadata, second.Metadata]);

        using var gateHold = await gate.EnterAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var mirror = persistence.MirrorMetadataAndSstsAsync(cancellation.Token);

        Assert.False(mirror.IsCompleted);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mirror.AsTask());
        Assert.False(File.Exists(Path.Combine(
            cache.Path,
            "cloud_store",
            "sst",
            second.Metadata.Name)));
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldRejectSimulatedSstBeforeUploadWhenPublicationBudgetDoesNotFit()
    {
        using var cache = new TemporaryDirectory();
        var sst = CreateSst(1, "a"u8.ToArray());
        WriteLocalFixture(cache.Path, [sst]);

        var budget = new ResourceBudget(128 * 1024);
        var admission = SstPublicationAdmission.Gated(budget, new MaintenanceMemoryGate());
        await Assert.ThrowsAsync<PantsResourceLimitException>(async () =>
        {
            await using var persistence = new SimulatedCloudPersistence(
                cache.Path,
                1,
                sstPublicationAdmission: admission);
        });

        Assert.False(Directory.Exists(Path.Combine(cache.Path, "cloud_store", "sst")));
        Assert.Equal(0, budget.Current);
        Assert.Equal(0, budget.Peak);
    }

    static (FileMeta Metadata, byte[] Bytes) CreateSst(ulong sequence, byte[] key)
        => CreateSst(sequence, key, 5);

    static (FileMeta Metadata, byte[] Bytes) CreateSst(
        ulong sequence,
        byte[] key,
        int valueBytes)
    {
        var value = new byte[valueBytes];
        new Random(checked((int)sequence)).NextBytes(value);
        var bytes = SstCodec.Encode(
            [new SstEntry(key, value, sequence, null, false)],
            [],
            PantsPerformanceGoal.Latency);
        var name = $"000000_00_{sequence:00000000000000000000}.sst";
        return (
            new FileMeta
            {
                Name = name,
                Level = 0,
                SizeBytes = checked((ulong)bytes.Length),
                ContentCrc32C = DiskFormat.Crc32C(bytes),
                ColumnFamilyId = 0,
                SstSequence = sequence,
                SmallestKey = key.ToArray(),
                LargestKey = key.ToArray(),
                SmallestSequence = sequence,
                LargestSequence = sequence
            },
            bytes);
    }

    static void WriteLocalFixture(
        string root,
        IReadOnlyList<(FileMeta Metadata, byte[] Bytes)> ssts)
    {
        Directory.CreateDirectory(Path.Combine(root, "sst"));
        File.WriteAllText(Path.Combine(root, "FORMAT"), "midge-format-version=9\n");
        File.WriteAllBytes(Path.Combine(root, "manifest.journal"), []);
        File.WriteAllBytes(Path.Combine(root, "intent_log.json"), []);
        foreach (var sst in ssts)
        {
            WriteSst(root, sst);
        }

        WriteManifest(root, ssts.Select(static sst => sst.Metadata).ToArray());
    }

    static void AddSstAndAdvanceManifest(
        string root,
        (FileMeta Metadata, byte[] Bytes) first,
        (FileMeta Metadata, byte[] Bytes) second)
    {
        WriteSst(root, second);
        WriteManifest(root, [first.Metadata, second.Metadata]);
    }

    static void WriteSst(
        string root,
        (FileMeta Metadata, byte[] Bytes) sst) =>
        AtomicStagedFile.Write(
            Path.Combine(root, "sst", sst.Metadata.Name),
            sst.Bytes);

    static void WriteManifest(string root, FileMeta[] files)
    {
        var manifest = new ManifestState
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
        var bytes = ManifestCodec.Encode(manifest, JsonOptions);
        AtomicStagedFile.Write(Path.Combine(root, "manifest.snapshot.json"), bytes);
        AtomicStagedFile.Write(Path.Combine(root, "manifest.json"), bytes);
    }

    static async Task AssertProviderManifestDependenciesAsync(
        SnapshotConsistencyCloudObjectStore controlStore,
        SnapshotConsistencyCloudObjectStore sstStore)
    {
        var files = await CloudMetadataAuthority.ReadCommittedFilesAsync(controlStore, CancellationToken.None);
        Assert.NotNull(files);
        var manifest = CloudManifestReader.DecodeMetadata(files);
        foreach (var file in manifest.Files)
        {
            var sst = Assert.IsType<byte[]>(sstStore.GetData(PantsCloudObjectLayout.SstPrefix + file.Name));
            CloudSstValidator.Validate(sst, file);
        }
    }

    static void AssertSimulatedManifestDependencies(string root)
    {
        var cloudRoot = Path.Combine(root, "cloud_store");
        var selected = CloudMetadataAuthority.ReadCommittedFilesAsync(new SimulatedCloudObjectStore(cloudRoot), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        var manifest = CloudManifestReader.DecodeMetadata(Assert.IsType<Dictionary<string, ReadOnlyMemory<byte>>>(selected));
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(cloudRoot, "sst", file.Name);
            Assert.True(File.Exists(path), $"Remote SST '{file.Name}' is missing.");
            CloudSstValidator.Validate(File.ReadAllBytes(path), file);
        }

    }
}
