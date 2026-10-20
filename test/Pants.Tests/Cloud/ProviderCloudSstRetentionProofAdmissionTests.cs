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
        controlStore.Write(
            PantsCloudObjectLayout.MetadataPrefix + "manifest.snapshot.json",
            EncodeManifest(nextSstSequence: 2));
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
        var manifestKey = PantsCloudObjectLayout.MetadataPrefix + "manifest.snapshot.json";
        controlStore.Write(manifestKey, new byte[1024]);
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
        var manifestKey = PantsCloudObjectLayout.MetadataPrefix + "manifest.snapshot.json";
        var manifestBytes = EncodeManifest(nextSstSequence: 2);
        var intentKey = PantsCloudObjectLayout.MetadataPrefix + "intent_log.json";
        byte[] intentBytes = "[]"u8.ToArray();
        controlStore.Write(manifestKey, manifestBytes);
        controlStore.Write(intentKey, intentBytes);
        var sstStore = new ScriptedCloudObjectStore();
        var objectKey = PantsCloudObjectLayout.SstPrefix + ObsoleteSstName;
        sstStore.Write(objectKey, "orphan"u8.ToArray());
        var manifestWorkspaceBytes = checked(manifestBytes.Length * 16L + 256);
        var intentWorkspaceBytes = checked(intentBytes.Length * 16L + 256);
        var budget = new ResourceBudget(manifestWorkspaceBytes + intentWorkspaceBytes);
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
        var manifestKey = PantsCloudObjectLayout.MetadataPrefix + "manifest.snapshot.json";
        var manifestBytes = EncodeManifest(nextSstSequence: 2);
        var intentKey = PantsCloudObjectLayout.MetadataPrefix + "intent_log.json";
        var intentBytes = "[]"u8.ToArray();
        controlStore.Write(manifestKey, manifestBytes);
        controlStore.Write(intentKey, intentBytes);
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
            checked((manifestBytes.Length * 16L + 256) + (intentBytes.Length * 16L + 256)),
            budgetAtIntentRead);
        Assert.Equal(0, budget.Current);
        Assert.DoesNotContain(sstStore.Requests,
            static request => request.Operation == StoreOperation.Delete);
        Assert.True(sstStore.Contains(objectKey));
    }

    static byte[] EncodeManifest(ulong nextSstSequence) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new ManifestState
            {
                NextSstSeqs = new Dictionary<uint, ulong> { [0] = nextSstSequence }
            },
            ManifestJsonOptions);

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
