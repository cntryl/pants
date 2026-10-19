using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderCloudDdlRegistryAdmissionTests
{
    const string RegistryKey = PantsCloudObjectLayout.DdlRegistryObjectKey;

    [Fact]
    public async Task ShouldRejectDdlRegistryReadBeforeBodyWhenSharedBudgetIsTooSmall()
    {
        using var cache = new TemporaryDirectory();
        using var lease = await AcquireLeaseAsync();
        var store = new ScriptedCloudObjectStore();
        store.Write(RegistryKey, new byte[64]);
        var budget = new ResourceBudget(512);
        await using var persistence = CreatePersistence(cache.Path, store, lease, budget);

        await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
            persistence.ReadDdlRegistryAsync(CancellationToken.None).AsTask());

        Assert.Equal(0, budget.Current);
        Assert.Contains(store.RequestsFor(RegistryKey),
            static request => request.Operation == StoreOperation.Head);
        Assert.DoesNotContain(store.RequestsFor(RegistryKey),
            static request => request.Operation is StoreOperation.Get or StoreOperation.GetRange);
    }

    [Fact]
    public async Task ShouldHoldDdlRegistryReadReservationUntilCallerReleasesSnapshot()
    {
        using var cache = new TemporaryDirectory();
        using var lease = await AcquireLeaseAsync();
        var store = new ScriptedCloudObjectStore();
        store.Write(RegistryKey, CloudDdlJson.SerializeRegistry(new CloudDdlRegistry()));
        var budget = new ResourceBudget(1024 * 1024);
        await using var persistence = CreatePersistence(cache.Path, store, lease, budget);

        var registry = Assert.IsType<CloudDdlRegistryObject>(
            await persistence.ReadDdlRegistryAsync(CancellationToken.None));

        Assert.True(budget.Current > 0);
        Assert.Contains(store.RequestsFor(RegistryKey),
            static request => request.Operation == StoreOperation.GetRange);
        Assert.DoesNotContain(store.RequestsFor(RegistryKey),
            static request => request.Operation == StoreOperation.Get);

        registry.Dispose();

        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldAccountForConcurrentDdlRegistryReadsAgainstOneBudget()
    {
        using var cache = new TemporaryDirectory();
        using var lease = await AcquireLeaseAsync();
        var store = new ScriptedCloudObjectStore();
        var bytes = CloudDdlJson.SerializeRegistry(new CloudDdlRegistry());
        store.Write(RegistryKey, bytes);
        var workspaceBytes = checked(bytes.Length * 16L + 256);
        var budget = new ResourceBudget(workspaceBytes + workspaceBytes / 2);
        var rangeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeRangeRead = async (_, cancellationToken) =>
        {
            rangeStarted.TrySetResult();
            await releaseRange.Task.WaitAsync(cancellationToken);
        };
        await using var persistence = CreatePersistence(cache.Path, store, lease, budget);

        var firstRead = persistence.ReadDdlRegistryAsync(CancellationToken.None).AsTask();
        await rangeStarted.Task.WaitAsync(TestTimeouts.Expected);

        await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
            persistence.ReadDdlRegistryAsync(CancellationToken.None).AsTask());

        Assert.Equal(workspaceBytes, budget.Current);

        releaseRange.TrySetResult();
        using var registry = Assert.IsType<CloudDdlRegistryObject>(await firstRead);
        Assert.Equal(workspaceBytes, budget.Current);
        Assert.Equal(1, store.RequestsFor(RegistryKey)
            .Count(static request => request.Operation == StoreOperation.GetRange));
        registry.Dispose();
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldRejectDdlRegistryPublicationBeforeCasWhenEnvelopeDoesNotFit()
    {
        using var cache = new TemporaryDirectory();
        using var lease = await AcquireLeaseAsync();
        var store = new ScriptedCloudObjectStore();
        var budget = new ResourceBudget(1024);
        await using var persistence = CreatePersistence(cache.Path, store, lease, budget);
        var submissionStarted = false;
        using var edit = JsonDocument.Parse(
            """{"CreateColumnFamily":{"id":1,"name":"bounded","created_at":1}}""");
        var registry = new CloudDdlRegistry
        {
            Operations =
            [
                new CloudDdlOperation
                {
                    OperationId = new string('x', 2048),
                    Edit = edit.RootElement.Clone()
                }
            ]
        };

        await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
            persistence.CompareExchangeDdlRegistryAsync(
                registry,
                null,
                CancellationToken.None,
                () => submissionStarted = true).AsTask());

        Assert.Equal(0, budget.Current);
        Assert.False(submissionStarted);
        Assert.DoesNotContain(store.RequestsFor(RegistryKey),
            static request => request.Operation == StoreOperation.Put);
        Assert.False(store.Contains(RegistryKey));
    }

    [Fact]
    public async Task ShouldMarkDdlSubmissionAfterAdmissionAndBeforeConditionalPut()
    {
        using var cache = new TemporaryDirectory();
        using var lease = await AcquireLeaseAsync();
        var store = new ScriptedCloudObjectStore();
        var budget = new ResourceBudget(1024 * 1024);
        await using var persistence = CreatePersistence(cache.Path, store, lease, budget);
        var submissionStarted = false;

        Assert.True(await persistence.CompareExchangeDdlRegistryAsync(
            new CloudDdlRegistry(),
            null,
            CancellationToken.None,
            () =>
            {
                submissionStarted = true;
                Assert.False(store.Contains(RegistryKey));
            }));

        Assert.True(submissionStarted);
        Assert.True(store.Contains(RegistryKey));
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldReportUnknownOutcomeWhenLargePublicationCannotBeRangeVerified()
    {
        using var cache = new TemporaryDirectory();
        using var lease = await AcquireLeaseAsync();
        var store = new ScriptedCloudObjectStore { SupportsRanges = false };
        var registry = CreateLargeRegistry();
        Assert.True(CloudDdlJson.SerializeRegistry(registry).Length >
                    ProviderCloudControlMetadataReadSet.MaximumWholeObjectBytes);
        var budget = new ResourceBudget(2 * 1024 * 1024);
        await using var persistence = CreatePersistence(cache.Path, store, lease, budget);
        var submissionStarted = false;

        await Assert.ThrowsAsync<PantsLeaseIndeterminateException>(() =>
            persistence.CompareExchangeDdlRegistryAsync(
                registry,
                null,
                CancellationToken.None,
                () => submissionStarted = true).AsTask());

        Assert.True(submissionStarted);
        Assert.True(store.Contains(RegistryKey));
        Assert.Contains(store.RequestsFor(RegistryKey),
            static request => request.Operation == StoreOperation.Put);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldFenceDdlRegistryWithBoundedReadbackAndConditionalPublication()
    {
        using var cache = new TemporaryDirectory();
        using var lease = await AcquireLeaseAsync();
        var store = new ScriptedCloudObjectStore();
        var budget = new ResourceBudget(1024 * 1024);
        await using var persistence = CreatePersistence(cache.Path, store, lease, budget);

        await persistence.FenceDdlRegistryAsync(new CloudDdlRegistry(), CancellationToken.None);

        Assert.True(store.Contains(RegistryKey));
        Assert.Equal(1, store.RequestsFor(RegistryKey)
            .Count(static request => request.Operation == StoreOperation.Put));
        Assert.Contains(store.RequestsFor(RegistryKey),
            static request => request.Operation == StoreOperation.GetRange);
        Assert.DoesNotContain(store.RequestsFor(RegistryKey),
            static request => request.Operation == StoreOperation.Get);
        Assert.Equal(0, budget.Current);
        var baseline = CloudDdlJson.SerializeRegistry(new CloudDdlRegistry());
        Assert.Equal(baseline.Length + (sizeof(ulong) * 8) + 2, store.Read(RegistryKey).Length);
        Assert.Equal(0UL, CloudDdlJson.DeserializeRegistry(store.Read(RegistryKey)).Epoch);
    }

    static async ValueTask<CloudLeaseCoordinator> AcquireLeaseAsync()
    {
        var lease = new CloudLeaseCoordinator(
            new TestCloudLeaseStore(),
            new ManualClock(DateTimeOffset.UnixEpoch),
            "writer",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        _ = await lease.AcquireAsync(CancellationToken.None);
        return lease;
    }

    static CloudDdlRegistry CreateLargeRegistry()
    {
        using var edit = JsonDocument.Parse(
            """{"CreateColumnFamily":{"id":1,"name":"bounded","created_at":1}}""");
        return new CloudDdlRegistry
        {
            Operations = Enumerable.Range(0, 400)
                .Select(index => new CloudDdlOperation
                {
                    OperationId = $"{index:D4}-{new string('x', 256)}",
                    Edit = edit.RootElement.Clone()
                })
                .ToList()
        };
    }

    static ProviderCloudPersistence CreatePersistence(
        string root,
        ScriptedCloudObjectStore controlStore,
        CloudLeaseCoordinator lease,
        ResourceBudget budget) =>
        new(
            root,
            new ScriptedCloudObjectStore(),
            new ScriptedCloudObjectStore(),
            controlStore,
            lease,
            walRetirementSettings: new WalRetirementSettings(
                budget,
                new MaintenanceMemoryGate(),
                WalRetirementSettings.DefaultPageBytes,
                WalRetirementSettings.DefaultQuantum,
                TimeProvider.System));
}
