using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderSstPublisherTests
{
    [Fact]
    public async Task ShouldPublishNewSstOnceAndNeverTouchItAgain()
    {
        using var directory = new TemporaryDirectory();
        var store = new RangeCountingCloudObjectStore();
        var publisher = new ProviderSstPublisher(store, static () => { });
        var path = WriteLocal(directory.Path, "a.sst", 200_000);

        await publisher.EnsurePublishedAsync("a.sst", path, [], CancellationToken.None);
        var callsAfterFirst = store.TotalCalls;
        await publisher.EnsurePublishedAsync("a.sst", path, [], CancellationToken.None);
        await publisher.EnsurePublishedAsync("a.sst", null, [], CancellationToken.None);

        Assert.Equal(1, store.Puts);
        Assert.Equal(callsAfterFirst, store.TotalCalls);
        Assert.Equal(0, store.WholeGets);
        Assert.True(store.RangeGets >= 4, "Verification should read the object in ranged pieces.");
        Assert.True(store.LargestRangeBytes <= ProviderSstPublisher.VerificationRangeBytes);
    }

    [Fact]
    public async Task ShouldIdentifyAlreadyPublishedSstByHeadWithoutDownloadingIt()
    {
        using var directory = new TemporaryDirectory();
        var store = new RangeCountingCloudObjectStore();
        var path = WriteLocal(directory.Path, "a.sst", 100_000);
        await new ProviderSstPublisher(store, static () => { })
            .EnsurePublishedAsync("a.sst", path, [], CancellationToken.None);
        store.ResetCounters();
        var restarted = new ProviderSstPublisher(store, static () => { });

        await restarted.EnsurePublishedAsync("a.sst", path, [], CancellationToken.None);
        await restarted.EnsurePublishedAsync("a.sst", null, [], CancellationToken.None);

        Assert.Equal(0, store.Puts);
        Assert.Equal(0, store.WholeGets);
        Assert.Equal(0, store.RangeGets);
    }

    [Fact]
    public async Task ShouldFailWhenRemoteOnlySstIsMissing()
    {
        var publisher = new ProviderSstPublisher(new RangeCountingCloudObjectStore(), static () => { });

        await Assert.ThrowsAsync<PantsRecoveryFailedException>(async () =>
            await publisher.EnsurePublishedAsync("gone.sst", null, [], CancellationToken.None));
    }

    [Fact]
    public async Task ShouldRejectVerificationWhenObjectIdentityChangesMidReadback()
    {
        using var directory = new TemporaryDirectory();
        var store = new RangeCountingCloudObjectStore { ChangeVersionOnRangeRead = true };
        var path = WriteLocal(directory.Path, "a.sst", 200_000);
        var publisher = new ProviderSstPublisher(store, static () => { });

        await Assert.ThrowsAsync<PantsFencedException>(async () =>
            await publisher.EnsurePublishedAsync("a.sst", path, [], CancellationToken.None));
        Assert.False(publisher.IsProven("a.sst"));
    }

    [Fact]
    public async Task ShouldAdmitUploadAndReadbackEnvelopeAgainstMaintenanceBudgetForNewSst()
    {
        using var directory = new TemporaryDirectory();
        var budget = new ResourceBudget(64L * 1024 * 1024);
        var store = new RangeCountingCloudObjectStore();
        var publisher = new ProviderSstPublisher(
            store,
            static () => { },
            SstPublicationAdmission.Gated(budget, new MaintenanceMemoryGate()));
        var path = WriteLocal(directory.Path, "a.sst", 200_000);

        await publisher.EnsurePublishedAsync("a.sst", path, [], CancellationToken.None);

        Assert.Equal(ImmutablePublicationEnvelope.For(200_000), budget.Peak);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldNotAdmitMemoryWhenSstIsAlreadyPublishedRemotely()
    {
        using var directory = new TemporaryDirectory();
        var store = new RangeCountingCloudObjectStore();
        var path = WriteLocal(directory.Path, "a.sst", 200_000);
        await new ProviderSstPublisher(store, static () => { })
            .EnsurePublishedAsync("a.sst", path, [], CancellationToken.None);
        var budget = new ResourceBudget(64L * 1024 * 1024);
        var restarted = new ProviderSstPublisher(
            store,
            static () => { },
            SstPublicationAdmission.Gated(budget, new MaintenanceMemoryGate()));

        await restarted.EnsurePublishedAsync("a.sst", path, [], CancellationToken.None);

        Assert.Equal(0, budget.Peak);
    }

    [Fact]
    public async Task ShouldWaitForInFlightCompactionRoundInsteadOfFailingSstPublication()
    {
        using var directory = new TemporaryDirectory();
        var budget = new ResourceBudget(16L * 1024 * 1024);
        var gate = new MaintenanceMemoryGate();
        var store = new RangeCountingCloudObjectStore();
        var publisher = new ProviderSstPublisher(
            store,
            static () => { },
            SstPublicationAdmission.Gated(budget, gate));
        var path = WriteLocal(directory.Path, "a.sst", 100_000);
        var compactionRound = await gate.EnterAsync(CancellationToken.None);
        var merge = budget.Reserve(budget.Limit);

        var publication = publisher.EnsurePublishedAsync("a.sst", path, [], CancellationToken.None)
            .AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.False(publication.IsCompleted);
        Assert.Equal(0, store.Puts);
        merge.Dispose();
        compactionRound.Dispose();
        await publication.WaitAsync(TestTimeouts.Expected);
        Assert.Equal(1, store.Puts);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldLeaveSstPendingWhenAdmissionIsCanceledBehindCompactionRound()
    {
        using var directory = new TemporaryDirectory();
        var budget = new ResourceBudget(16L * 1024 * 1024);
        var gate = new MaintenanceMemoryGate();
        var store = new RangeCountingCloudObjectStore();
        var publisher = new ProviderSstPublisher(
            store,
            static () => { },
            SstPublicationAdmission.Gated(budget, gate));
        var path = WriteLocal(directory.Path, "a.sst", 200_000);
        var original = File.ReadAllBytes(path);
        using (await gate.EnterAsync(CancellationToken.None))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await publisher.EnsurePublishedAsync("a.sst", path, [], cancellation.Token));
        }

        Assert.Equal(1, store.TotalCalls);
        Assert.False(publisher.IsProven("a.sst"));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(0, budget.Current);

        await publisher.EnsurePublishedAsync("a.sst", path, [], CancellationToken.None);

        Assert.True(publisher.IsProven("a.sst"));
        Assert.Equal(1, store.Puts);
    }

    [Fact]
    public async Task ShouldClaimWholeMaintenancePoolForFlushOutputLargerThanItsEnvelope()
    {
        using var directory = new TemporaryDirectory();
        var budget = new ResourceBudget(512 * 1024);
        var store = new RangeCountingCloudObjectStore();
        var publisher = new ProviderSstPublisher(
            store,
            static () => { },
            SstPublicationAdmission.Gated(budget, new MaintenanceMemoryGate()));
        var path = WriteLocal(directory.Path, "a.sst", 200_000);

        await publisher.EnsurePublishedAsync("a.sst", path, [], CancellationToken.None);

        Assert.True(publisher.IsProven("a.sst"));
        Assert.Equal(budget.Limit, budget.Peak);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldClaimWholeMaintenancePoolForCompactionOutputLargerThanItsEnvelope()
    {
        using var directory = new TemporaryDirectory();
        var budget = new ResourceBudget(512 * 1024);
        var store = new RangeCountingCloudObjectStore();
        var publisher = new ProviderSstPublisher(
            store,
            static () => { },
            SstPublicationAdmission.WithinCompactionRound(budget));
        var path = WriteLocal(directory.Path, "a.sst", 200_000);

        await publisher.PublishOutputAsync("a.sst", path, CancellationToken.None);

        Assert.Equal(1, store.Puts);
        Assert.Equal(budget.Limit, budget.Peak);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldVerifyCompactionOutputWithBoundedIdentityPinnedRanges()
    {
        using var directory = new TemporaryDirectory();
        var budget = new ResourceBudget(64L * 1024 * 1024);
        var store = new RangeCountingCloudObjectStore();
        var publisher = new ProviderSstPublisher(
            store,
            static () => { },
            SstPublicationAdmission.WithinCompactionRound(budget));
        var path = WriteLocal(directory.Path, "a.sst", 300_000);

        await publisher.PublishOutputAsync("a.sst", path, CancellationToken.None);

        Assert.Equal(1, store.Puts);
        Assert.Equal(0, store.WholeGets);
        Assert.True(store.RangeGets >= 5, "Verification should read the object in ranged pieces.");
        Assert.True(store.LargestRangeBytes <= ProviderSstPublisher.VerificationRangeBytes);
        Assert.Equal(ImmutablePublicationEnvelope.For(300_000), budget.Peak);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldRejectCompactionOutputWhoseIdentityChangesMidReadback()
    {
        using var directory = new TemporaryDirectory();
        var store = new RangeCountingCloudObjectStore { ChangeVersionOnRangeRead = true };
        var publisher = new ProviderSstPublisher(store, static () => { });
        var path = WriteLocal(directory.Path, "a.sst", 200_000);

        await Assert.ThrowsAsync<PantsFencedException>(async () =>
            await publisher.PublishOutputAsync("a.sst", path, CancellationToken.None));
    }

    [Fact]
    public async Task ShouldRejectCompactionOutputThatConflictsWithExistingRemoteObject()
    {
        using var directory = new TemporaryDirectory();
        var store = new RangeCountingCloudObjectStore();
        var publisher = new ProviderSstPublisher(store, static () => { });
        var first = WriteLocal(directory.Path, "first.sst", 100_000);
        await publisher.PublishOutputAsync("a.sst", first, CancellationToken.None);
        var second = WriteLocal(directory.Path, "second.sst", 100_000);
        store.ResetCounters();

        await Assert.ThrowsAsync<PantsFencedException>(async () =>
            await publisher.PublishOutputAsync("a.sst", second, CancellationToken.None));
        Assert.Equal(0, store.Puts);
        Assert.Equal(0, store.WholeGets);
    }

    static string WriteLocal(string directory, string name, int bytes)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes));
        return path;
    }
}
