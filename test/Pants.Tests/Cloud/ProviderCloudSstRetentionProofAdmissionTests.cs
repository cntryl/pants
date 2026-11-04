using System.Text.Json;
using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderCloudSstRetentionProofAdmissionTests
{
    const string ObsoleteSstName = "000000_01_00000000000000000001.sst";
    static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public async Task ShouldKeepSstRetentionProofMemoryChargedUntilCollectionFinishes()
    {
        using var cache = new TemporaryDirectory();
        using var lease = await AcquireLeaseAsync();
        var controlStore = new ScriptedCloudObjectStore();
        await SeedRetentionMetadataAsync(controlStore, EncodeManifest(nextSstSequence: 2));
        var sstStore = new ScriptedCloudObjectStore();
        var objectKey = PantsCloudObjectLayout.SstPrefix + ObsoleteSstName;
        sstStore.Write(objectKey, "orphan"u8.ToArray());
        var walStore = new ScriptedCloudObjectStore();
        var budget = new ResourceBudget(1024 * 1024);
        long budgetAtDelete = 0;
        var failpoints = new CloudCompactionFailpointHandler();
        failpoints.ArmCallback(
            Failpoint.BeforeCloudSstGarbageCollectionDelete,
            () => budgetAtDelete = budget.Current);
        await using var persistence = CreatePersistence(
            cache.Path,
            walStore,
            sstStore,
            controlStore,
            lease,
            budget,
            failpoints);

        await persistence.CollectObsoleteSstsAsync(CancellationToken.None);

        Assert.True(budgetAtDelete > 0);
        Assert.True(budget.Peak > 0);
        Assert.Equal(0, budget.Current);
        Assert.False(sstStore.Contains(objectKey));
    }

    [Fact]
    public async Task ShouldRejectSstRetentionProofBeforeReadingBodyWhenMemoryDoesNotFit()
    {
        using var cache = new TemporaryDirectory();
        using var lease = await AcquireLeaseAsync();
        var controlStore = new ScriptedCloudObjectStore();
        var seeded = await SeedRetentionMetadataAsync(controlStore, new byte[1024]);
        var manifestKey = CloudMetadataAuthority.PointerKey;
        var sstStore = new ScriptedCloudObjectStore();
        var objectKey = PantsCloudObjectLayout.SstPrefix + ObsoleteSstName;
        sstStore.Write(objectKey, "orphan"u8.ToArray());
        var budget = new ResourceBudget(1024);
        await using var persistence = CreatePersistence(
            cache.Path,
            new ScriptedCloudObjectStore(),
            sstStore,
            controlStore,
            lease,
            budget);

        await persistence.CollectObsoleteSstsAsync(CancellationToken.None);

        Assert.True(persistence.HasPersistenceAnomaly);
        Assert.Equal(0, budget.Current);
        Assert.Contains(controlStore.RequestsFor(manifestKey),
            static request => request.Operation == StoreOperation.Head);
        Assert.DoesNotContain(controlStore.RequestsFor(manifestKey),
            static request => request.Operation is StoreOperation.Get or StoreOperation.GetRange);
        Assert.DoesNotContain(sstStore.Requests,
            static request => request.Operation == StoreOperation.Delete);
        Assert.True(sstStore.Contains(objectKey));
    }

    [Fact]
    public async Task ShouldChargeAllSstRetentionProofReadsToOneSharedBudgetAndRetryAfterRelease()
    {
        using var cache = new TemporaryDirectory();
        using var lease = await AcquireLeaseAsync();
        var controlStore = new ScriptedCloudObjectStore();
        var manifestBytes = EncodeManifest(nextSstSequence: 2);
        var intentBytes = IntentLogCodec.CreateEdit(IntentLogCodec.Read([]), []);
        var seeded = await SeedRetentionMetadataAsync(controlStore, manifestBytes, intentBytes);
        var manifestKey = seeded.ManifestKey;
        var intentKey = seeded.IntentKey!;
        var sstStore = new ScriptedCloudObjectStore();
        var objectKey = PantsCloudObjectLayout.SstPrefix + ObsoleteSstName;
        sstStore.Write(objectKey, "orphan"u8.ToArray());
        var budget = new ResourceBudget(seeded.WorkspaceBytes);
        await using var persistence = CreatePersistence(
            cache.Path,
            new ScriptedCloudObjectStore(),
            sstStore,
            controlStore,
            lease,
            budget);
        using var competingReservation = budget.Reserve(1);

        await persistence.CollectObsoleteSstsAsync(CancellationToken.None);

        Assert.Equal(1, budget.Current);
        Assert.Contains(controlStore.RequestsFor(manifestKey),
            static request => request.Operation == StoreOperation.GetRange);
        Assert.Contains(controlStore.RequestsFor(intentKey),
            static request => request.Operation == StoreOperation.Head);
        Assert.DoesNotContain(controlStore.RequestsFor(intentKey),
            static request => request.Operation is StoreOperation.Get or StoreOperation.GetRange);
        Assert.DoesNotContain(sstStore.Requests,
            static request => request.Operation == StoreOperation.Delete);
        Assert.True(sstStore.Contains(objectKey));

        competingReservation.Dispose();
        controlStore.ResetRequests();

        await persistence.CollectObsoleteSstsAsync(CancellationToken.None);

        Assert.Contains(controlStore.RequestsFor(manifestKey),
            static request => request.Operation == StoreOperation.GetRange);
        Assert.Contains(controlStore.RequestsFor(intentKey),
            static request => request.Operation == StoreOperation.GetRange);
        Assert.False(sstStore.Contains(objectKey));
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldReleaseSstRetentionProofReservationsWhenIntentReadIsCancelled()
    {
        using var cache = new TemporaryDirectory();
        using var lease = await AcquireLeaseAsync();
        var controlStore = new ScriptedCloudObjectStore();
        var manifestBytes = EncodeManifest(nextSstSequence: 2);
        var intentBytes = IntentLogCodec.CreateEdit(IntentLogCodec.Read([]), []);
        var seeded = await SeedRetentionMetadataAsync(controlStore, manifestBytes, intentBytes);
        var manifestKey = seeded.ManifestKey;
        var intentKey = seeded.IntentKey!;
        var budget = new ResourceBudget(1024 * 1024);
        var intentReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var budgetAtIntentRead = 0L;
        controlStore.BeforeRangeRead = async (objectKey, cancellationToken) =>
        {
            if (!StringComparer.Ordinal.Equals(objectKey, intentKey))
            {
                return;
            }

            budgetAtIntentRead = budget.Current;
            intentReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        var sstStore = new ScriptedCloudObjectStore();
        var objectKey = PantsCloudObjectLayout.SstPrefix + ObsoleteSstName;
        sstStore.Write(objectKey, "orphan"u8.ToArray());
        await using var persistence = CreatePersistence(
            cache.Path,
            new ScriptedCloudObjectStore(),
            sstStore,
            controlStore,
            lease,
            budget);
        using var cancellation = new CancellationTokenSource();

        var collection = persistence.CollectObsoleteSstsAsync(cancellation.Token).AsTask();
        await intentReadStarted.Task.WaitAsync(TestTimeouts.Expected);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collection);

        Assert.Equal(
            seeded.WorkspaceBytes,
            budgetAtIntentRead);
        Assert.Equal(0, budget.Current);
        Assert.DoesNotContain(sstStore.Requests,
            static request => request.Operation == StoreOperation.Delete);
        Assert.True(sstStore.Contains(objectKey));
    }

    [Fact]
    public async Task ShouldReleaseSstRetentionProofReservationsWhenIntentChangesDuringCapture()
    {
        using var cache = new TemporaryDirectory();
        using var lease = await AcquireLeaseAsync();
        var controlStore = new ScriptedCloudObjectStore();
        var manifestBytes = EncodeManifest(nextSstSequence: 2);
        var intentBytes = IntentLogCodec.CreateEdit(IntentLogCodec.Read([]), []);
        var seeded = await SeedRetentionMetadataAsync(controlStore, manifestBytes, intentBytes);
        var manifestKey = seeded.ManifestKey;
        var intentKey = seeded.IntentKey!;
        var budget = new ResourceBudget(1024 * 1024);
        var budgetAtIntentRead = 0L;
        controlStore.BeforeRangeRead = (objectKey, _) =>
        {
            if (StringComparer.Ordinal.Equals(objectKey, intentKey))
            {
                budgetAtIntentRead = budget.Current;
                controlStore.Write(intentKey, intentBytes);
            }

            return ValueTask.CompletedTask;
        };
        var sstStore = new ScriptedCloudObjectStore();
        var objectKey = PantsCloudObjectLayout.SstPrefix + ObsoleteSstName;
        sstStore.Write(objectKey, "orphan"u8.ToArray());
        await using var persistence = CreatePersistence(
            cache.Path,
            new ScriptedCloudObjectStore(),
            sstStore,
            controlStore,
            lease,
            budget);

        await persistence.CollectObsoleteSstsAsync(CancellationToken.None);

        Assert.True(persistence.HasPersistenceAnomaly);
        Assert.Equal(
            seeded.WorkspaceBytes,
            budgetAtIntentRead);
        Assert.Equal(0, budget.Current);
        Assert.DoesNotContain(sstStore.Requests,
            static request => request.Operation == StoreOperation.Delete);
        Assert.True(sstStore.Contains(objectKey));
    }

    [Fact]
    public async Task ShouldReleaseSstRetentionProofReservationsWhenProviderReadFails()
    {
        using var cache = new TemporaryDirectory();
        using var lease = await AcquireLeaseAsync();
        var controlStore = new ScriptedCloudObjectStore();
        var manifestBytes = EncodeManifest(nextSstSequence: 2);
        await SeedRetentionMetadataAsync(controlStore, manifestBytes);
        controlStore.BeforeRangeRead = static (_, _) =>
            ValueTask.FromException(new PantsIOException("synthetic retention proof read failure"));
        var sstStore = new ScriptedCloudObjectStore();
        var objectKey = PantsCloudObjectLayout.SstPrefix + ObsoleteSstName;
        sstStore.Write(objectKey, "orphan"u8.ToArray());
        var budget = new ResourceBudget(1024 * 1024);
        await using var persistence = CreatePersistence(
            cache.Path,
            new ScriptedCloudObjectStore(),
            sstStore,
            controlStore,
            lease,
            budget);

        await persistence.CollectObsoleteSstsAsync(CancellationToken.None);

        Assert.True(persistence.HasPersistenceAnomaly);
        Assert.Equal(0, budget.Current);
        Assert.DoesNotContain(sstStore.Requests,
            static request => request.Operation == StoreOperation.Delete);
        Assert.True(sstStore.Contains(objectKey));
    }

    [Fact]
    public async Task ShouldReleaseSstRetentionProofReservationsWhenProviderReturnsAShortRange()
    {
        using var cache = new TemporaryDirectory();
        using var lease = await AcquireLeaseAsync();
        var controlStore = new ScriptedCloudObjectStore { ShortenRangeByBytes = 1 };
        await SeedRetentionMetadataAsync(controlStore, EncodeManifest(nextSstSequence: 2));
        var sstStore = new ScriptedCloudObjectStore();
        var objectKey = PantsCloudObjectLayout.SstPrefix + ObsoleteSstName;
        sstStore.Write(objectKey, "orphan"u8.ToArray());
        var budget = new ResourceBudget(1024 * 1024);
        await using var persistence = CreatePersistence(
            cache.Path,
            new ScriptedCloudObjectStore(),
            sstStore,
            controlStore,
            lease,
            budget);

        await persistence.CollectObsoleteSstsAsync(CancellationToken.None);

        Assert.True(persistence.HasPersistenceAnomaly);
        Assert.Equal(0, budget.Current);
        Assert.DoesNotContain(sstStore.Requests,
            static request => request.Operation == StoreOperation.Delete);
        Assert.True(sstStore.Contains(objectKey));
    }

    [Fact]
    public async Task ShouldReleaseSstRetentionProofReservationsWhenManifestDecodeFails()
    {
        using var cache = new TemporaryDirectory();
        using var lease = await AcquireLeaseAsync();
        var controlStore = new ScriptedCloudObjectStore();
        await SeedRetentionMetadataAsync(controlStore, "{ torn"u8.ToArray());
        var sstStore = new ScriptedCloudObjectStore();
        var objectKey = PantsCloudObjectLayout.SstPrefix + ObsoleteSstName;
        sstStore.Write(objectKey, "orphan"u8.ToArray());
        var budget = new ResourceBudget(1024 * 1024);
        await using var persistence = CreatePersistence(
            cache.Path,
            new ScriptedCloudObjectStore(),
            sstStore,
            controlStore,
            lease,
            budget);

        await persistence.CollectObsoleteSstsAsync(CancellationToken.None);

        Assert.True(persistence.HasPersistenceAnomaly);
        Assert.Equal(0, budget.Current);
        Assert.DoesNotContain(sstStore.Requests,
            static request => request.Operation == StoreOperation.Delete);
        Assert.True(sstStore.Contains(objectKey));
    }

    static byte[] EncodeManifest(ulong nextSstSequence) =>
        ManifestCodec.Encode(
            new ManifestState
            {
                NextSstSeqs = new Dictionary<uint, ulong> { [0] = nextSstSequence }
            },
            ManifestJsonOptions);

    static async Task<(string ManifestKey, string? IntentKey, long WorkspaceBytes)> SeedRetentionMetadataAsync(
        ScriptedCloudObjectStore store, byte[] manifestBytes, byte[]? intentBytes = null)
    {
        await CloudTestMetadata.SeedAsync(store, ManifestState.CreateInitial(), manifestBytes, intentBytes);
        using var pointer = MetadataFrameCodec.Decode(store.Read(CloudMetadataAuthority.PointerKey), 4);
        var objects = pointer.RootElement.GetProperty("committed").GetProperty("objects").EnumerateArray().ToArray();
        var manifestKey = objects.Single(static value => value.GetProperty("file_name").GetString() == "manifest.snapshot.json").GetProperty("object_key").GetString()!;
        var intentKey = objects.Where(static value => value.GetProperty("file_name").GetString() == "intent_log.json").Select(static value => value.GetProperty("object_key").GetString()).SingleOrDefault();
        var workspace = checked((store.Read(CloudMetadataAuthority.PointerKey).Length + store.Read(CloudMetadataAuthority.SentinelKey).Length) * 16L + 512);
        foreach (var item in objects)
        {
            workspace = checked(workspace + item.GetProperty("len").GetInt64() * 16 + 256);
        }

        store.ResetRequests();
        return (manifestKey, intentKey, workspace);
    }

    static async ValueTask<CloudLeaseCoordinator> AcquireLeaseAsync()
    {
        var lease = new CloudLeaseCoordinator(
            new TestCloudLeaseStore(),
            new ManualClock(DateTimeOffset.UnixEpoch),
            "writer",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        await lease.AcquireAsync(CancellationToken.None);
        return lease;
    }

    static ProviderCloudPersistence CreatePersistence(
        string root,
        ICloudObjectStore walStore,
        ICloudObjectStore sstStore,
        ICloudObjectStore controlStore,
        CloudLeaseCoordinator lease,
        ResourceBudget budget,
        IFailpointHandler? failpoints = null) => new(
        root,
        walStore,
        sstStore,
        controlStore,
        lease,
        failpoints,
        walRetirementSettings: new WalRetirementSettings(
            budget,
            new MaintenanceMemoryGate(),
            WalRetirementSettings.DefaultPageBytes,
            WalRetirementSettings.DefaultQuantum,
            TimeProvider.System));
}
