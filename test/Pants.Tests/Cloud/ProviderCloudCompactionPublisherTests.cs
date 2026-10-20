using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderCloudCompactionPublisherTests
{
    const string OutputName = "000000_01_00000000000000000002.sst";

    [Fact]
    public async Task ShouldRejectCompactionIntentBeforeProviderReadsWhenMetadataBudgetDoesNotFit()
    {
        using var cache = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(cache.Path, "sst"));
        await File.WriteAllBytesAsync(
            Path.Combine(cache.Path, "intent_log.json"),
            new byte[128 * 1024]);
        using var lease = await AcquireLeaseAsync();
        var controlStore = new ScriptedCloudObjectStore();
        var budget = new ResourceBudget(64 * 1024);
        var publisher = new ProviderCloudCompactionPublisher(
            cache.Path,
            new RangeCountingCloudObjectStore(),
            controlStore,
            lease,
            NullPantsFailpointHandler.Instance,
            SstPublicationAdmission.WithinCompactionRound(budget));

        await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
            publisher.PublishAsync([], CancellationToken.None).AsTask());

        Assert.Empty(controlStore.Requests);
        Assert.False(controlStore.Contains(
            PantsCloudObjectLayout.MetadataPrefix + "intent_log.json"));
        Assert.Equal(0, budget.Current);
        Assert.Equal(0, budget.Peak);
    }

    [Fact]
    public async Task ShouldPublishAdmittedCompactionOutputWithBoundedRangedReadback()
    {
        using var cache = new TemporaryDirectory();
        var (intent, _) = WriteCompactionState(cache.Path, 300_000);
        using var lease = await AcquireLeaseAsync();
        var sstStore = new RangeCountingCloudObjectStore();
        var controlStore = new ScriptedCloudObjectStore();
        var intentKey = PantsCloudObjectLayout.MetadataPrefix + "intent_log.json";
        controlStore.Write(intentKey, intent);
        var budget = new ResourceBudget(64L * 1024 * 1024);
        var publisher = new ProviderCloudCompactionPublisher(
            cache.Path,
            sstStore,
            controlStore,
            lease,
            NullPantsFailpointHandler.Instance,
            SstPublicationAdmission.WithinCompactionRound(budget));

        await publisher.PublishAsync([OutputName], CancellationToken.None);

        Assert.Equal(1, sstStore.Puts);
        Assert.Equal(0, sstStore.WholeGets);
        Assert.True(sstStore.LargestRangeBytes <= ProviderSstPublisher.VerificationRangeBytes);
        var intentRequests = controlStore.RequestsFor(intentKey).ToArray();
        Assert.Equal(2, intentRequests.Count(static request => request.Operation == StoreOperation.Head));
        Assert.Equal(2, intentRequests.Count(static request => request.Operation == StoreOperation.GetRange));
        Assert.DoesNotContain(intentRequests, static request => request.Operation == StoreOperation.Get);
        Assert.All(
            intentRequests.Where(static request => request.Operation == StoreOperation.GetRange),
            static request => Assert.InRange(
                request.Length,
                1,
                ProviderCloudControlMetadataReadSet.ReadRangeBytes));
        Assert.Equal(ImmutablePublicationEnvelope.For(300_000), budget.Peak);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldHoldMaintenanceBudgetForBothIntentReadsUntilReadbackCompletes()
    {
        using var cache = new TemporaryDirectory();
        var (intent, _) = WriteCompactionState(cache.Path, 128);
        using var lease = await AcquireLeaseAsync();
        var controlStore = new ScriptedCloudObjectStore();
        var intentKey = PantsCloudObjectLayout.MetadataPrefix + "intent_log.json";
        controlStore.Write(intentKey, intent);
        var budget = new ResourceBudget(1024 * 1024);
        var publisher = new ProviderCloudCompactionPublisher(
            cache.Path,
            new RangeCountingCloudObjectStore(),
            controlStore,
            lease,
            NullPantsFailpointHandler.Instance,
            SstPublicationAdmission.WithinCompactionRound(budget));

        await publisher.PublishAsync([], CancellationToken.None);

        var readWorkspaceBytes = (long)intent.Length * 16 + 256;
        Assert.Equal(
            ImmutablePublicationEnvelope.For(intent.Length) + (2 * readWorkspaceBytes),
            budget.Peak);
        Assert.Equal(0, budget.Current);
        Assert.Equal(intent, controlStore.Read(intentKey));
    }

    [Fact]
    public async Task ShouldRetainCompactionIntentAndRetryWhenOutputEnvelopeExceedsMaintenancePool()
    {
        using var cache = new TemporaryDirectory();
        var (intent, output) = WriteCompactionState(cache.Path, 200_000);
        using var lease = await AcquireLeaseAsync();
        var sstStore = new RangeCountingCloudObjectStore();
        var controlStore = new ScriptedCloudObjectStore();
        var undersizedBudget = new ResourceBudget(512 * 1024);
        var blockedPublisher = new ProviderCloudCompactionPublisher(
            cache.Path,
            sstStore,
            controlStore,
            lease,
            NullPantsFailpointHandler.Instance,
            SstPublicationAdmission.WithinCompactionRound(undersizedBudget));

        await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
            blockedPublisher.PublishAsync([OutputName], CancellationToken.None).AsTask());

        var intentKey = PantsCloudObjectLayout.MetadataPrefix + "intent_log.json";
        var outputPath = Path.Combine(cache.Path, "sst", OutputName);
        Assert.Equal(intent, File.ReadAllBytes(Path.Combine(cache.Path, "intent_log.json")));
        Assert.True(controlStore.Contains(intentKey));
        Assert.Equal(intent, controlStore.Read(intentKey));
        Assert.Equal(output, File.ReadAllBytes(outputPath));
        Assert.Equal(0, sstStore.Puts);
        Assert.Equal(0, undersizedBudget.Current);

        var retryBudget = new ResourceBudget(2 * 1024 * 1024);
        var retryPublisher = new ProviderCloudCompactionPublisher(
            cache.Path,
            sstStore,
            controlStore,
            lease,
            NullPantsFailpointHandler.Instance,
            SstPublicationAdmission.WithinCompactionRound(retryBudget));
        await retryPublisher.PublishAsync([OutputName], CancellationToken.None);

        Assert.Equal(1, sstStore.Puts);
        Assert.Equal(output, File.ReadAllBytes(outputPath));
        Assert.Equal(intent, controlStore.Read(intentKey));
        Assert.Equal(ImmutablePublicationEnvelope.For(output.Length), retryBudget.Peak);
        Assert.Equal(0, retryBudget.Current);
    }

    [Fact]
    public async Task ShouldFenceCompactionIntentWriteWhenSuccessorTakesLeaseDuringPreCasRead()
    {
        using var cache = new TemporaryDirectory();
        var (intent, _) = WriteCompactionState(cache.Path, 128);
        var leaseStore = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var lease = new CloudLeaseCoordinator(
            leaseStore,
            clock,
            "predecessor",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        var epoch = await lease.AcquireAsync(CancellationToken.None);
        var controlStore = new SnapshotConsistencyCloudObjectStore();
        var intentKey = PantsCloudObjectLayout.MetadataPrefix + "intent_log.json";
        controlStore.Seed(intentKey, intent);
        var getStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controlStore.BeforeGetAsync = async (objectKey, _, cancellationToken) =>
        {
            if (StringComparer.Ordinal.Equals(objectKey, intentKey))
            {
                getStarted.TrySetResult();
                await releaseGet.Task.WaitAsync(cancellationToken);
            }
        };
        var publisher = new ProviderCloudCompactionPublisher(
            cache.Path,
            new RangeCountingCloudObjectStore(),
            controlStore,
            lease,
            NullPantsFailpointHandler.Instance,
            SstPublicationAdmission.WithinCompactionRound(new ResourceBudget(1024 * 1024)));

        var publication = publisher.PublishAsync([], CancellationToken.None).AsTask();
        try
        {
            await getStarted.Task.WaitAsync(TestTimeouts.Expected);
            leaseStore.Seed(new CloudLeaseRecord(
                "successor",
                epoch + 1,
                "successor-token",
                clock.UtcNow,
                clock.UtcNow + TimeSpan.FromSeconds(10)));
        }
        finally
        {
            releaseGet.TrySetResult();
        }

        await Assert.ThrowsAsync<PantsFencedException>(() => publication);

        Assert.Equal(intent, controlStore.GetData(intentKey));
        Assert.False(lease.IsHealthy);
    }

    static (byte[] Intent, byte[] Output) WriteCompactionState(string root, int outputBytes)
    {
        var intent = "{\"entries\":[]}"u8.ToArray();
        File.WriteAllBytes(Path.Combine(root, "intent_log.json"), intent);
        Directory.CreateDirectory(Path.Combine(root, "sst"));
        var output = System.Security.Cryptography.RandomNumberGenerator.GetBytes(outputBytes);
        File.WriteAllBytes(Path.Combine(root, "sst", OutputName), output);
        return (intent, output);
    }

    static async Task<CloudLeaseCoordinator> AcquireLeaseAsync()
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
}
