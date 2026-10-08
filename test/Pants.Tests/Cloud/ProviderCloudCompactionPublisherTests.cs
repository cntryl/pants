using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderCloudCompactionPublisherTests
{
    const string OutputName = "000000_01_00000000000000000002.sst";

    [Fact]
    public async Task ShouldRetainCompactionIntentAndOutputWhilePublicationCannotBeAdmitted()
    {
        using var cache = new TemporaryDirectory();
        var (intent, output) = WriteCompactionState(cache.Path, 200_000);
        using var lease = await AcquireLeaseAsync();
        var sstStore = new RangeCountingCloudObjectStore();
        var budget = new ResourceBudget(16L * 1024 * 1024);
        var publisher = new ProviderCloudCompactionPublisher(
            cache.Path,
            sstStore,
            new CountingCloudObjectStore(),
            lease,
            NullPantsFailpointHandler.Instance,
            new SstPublicationAdmission(budget));
        var held = budget.Reserve(budget.Limit);

        var publication = publisher.PublishAsync([OutputName], CancellationToken.None).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.False(publication.IsCompleted);
        Assert.Equal(0, sstStore.TotalCalls);
        Assert.Equal(intent, File.ReadAllBytes(Path.Combine(cache.Path, "intent_log.json")));
        Assert.Equal(output, File.ReadAllBytes(Path.Combine(cache.Path, "sst", OutputName)));
        held.Dispose();
        await publication.WaitAsync(TestTimeouts.Expected);
        Assert.Equal(1, sstStore.Puts);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldRepublishRetainedCompactionOutputAfterCanceledAdmission()
    {
        using var cache = new TemporaryDirectory();
        var (_, output) = WriteCompactionState(cache.Path, 200_000);
        using var lease = await AcquireLeaseAsync();
        var sstStore = new RangeCountingCloudObjectStore();
        var budget = new ResourceBudget(16L * 1024 * 1024);
        var publisher = new ProviderCloudCompactionPublisher(
            cache.Path,
            sstStore,
            new CountingCloudObjectStore(),
            lease,
            NullPantsFailpointHandler.Instance,
            new SstPublicationAdmission(budget));
        using (budget.Reserve(budget.Limit))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await publisher.PublishAsync([OutputName], cancellation.Token));
        }

        Assert.Equal(0, sstStore.TotalCalls);
        Assert.Equal(output, File.ReadAllBytes(Path.Combine(cache.Path, "sst", OutputName)));

        await publisher.PublishAsync([OutputName], CancellationToken.None);

        Assert.Equal(1, sstStore.Puts);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldPublishAdmittedCompactionOutputWithBoundedRangedReadback()
    {
        using var cache = new TemporaryDirectory();
        _ = WriteCompactionState(cache.Path, 300_000);
        using var lease = await AcquireLeaseAsync();
        var sstStore = new RangeCountingCloudObjectStore();
        var budget = new ResourceBudget(64L * 1024 * 1024);
        var publisher = new ProviderCloudCompactionPublisher(
            cache.Path,
            sstStore,
            new CountingCloudObjectStore(),
            lease,
            NullPantsFailpointHandler.Instance,
            new SstPublicationAdmission(budget));

        await publisher.PublishAsync([OutputName], CancellationToken.None);

        Assert.Equal(1, sstStore.Puts);
        Assert.Equal(0, sstStore.WholeGets);
        Assert.True(sstStore.LargestRangeBytes <= ProviderSstPublisher.VerificationRangeBytes);
        Assert.Equal(ImmutablePublicationEnvelope.For(300_000), budget.Peak);
        Assert.Equal(0, budget.Current);
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
