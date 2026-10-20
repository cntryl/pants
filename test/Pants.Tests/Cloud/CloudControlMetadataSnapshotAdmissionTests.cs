using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudControlMetadataSnapshotAdmissionTests
{
    [Fact]
    public async Task ShouldRejectMetadataSnapshotBeforeProviderWriteWhenBudgetDoesNotFit()
    {
        using var cache = new TemporaryDirectory();
        File.WriteAllBytes(Path.Combine(cache.Path, "manifest.journal"), new byte[64]);
        using var lease = await AcquireLeaseAsync();
        var controlStore = new TestCloudObjectStore();
        var budget = new ResourceBudget(1024);
        await using var persistence = CreatePersistence(cache.Path, controlStore, lease, budget);

        await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
            persistence.MirrorMetadataAndSstsAsync(CancellationToken.None).AsTask());

        Assert.Equal(0, controlStore.PutCount);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldKeepMetadataSnapshotReservedUntilProviderMirrorCompletes()
    {
        using var cache = new TemporaryDirectory();
        const int journalBytes = 64;
        File.WriteAllBytes(Path.Combine(cache.Path, "manifest.journal"), new byte[journalBytes]);
        using var lease = await AcquireLeaseAsync();
        var controlStore = new TestCloudObjectStore();
        var budget = new ResourceBudget(1024 * 1024);
        var putStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controlStore.BeforeNextPutAsync = async cancellationToken =>
        {
            putStarted.TrySetResult();
            await releasePut.Task.WaitAsync(cancellationToken);
        };
        await using var persistence = CreatePersistence(cache.Path, controlStore, lease, budget);

        var mirror = persistence.MirrorMetadataAndSstsAsync(CancellationToken.None).AsTask();
        try
        {
            await putStarted.Task.WaitAsync(TestTimeouts.Expected);
            Assert.Equal(journalBytes * 16L + 256, budget.Current);
        }
        finally
        {
            releasePut.TrySetResult();
        }

        await mirror;
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldReleaseSnapshotReservationWhenManifestDecodeFails()
    {
        using var cache = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(cache.Path, "manifest.json"), "{");
        using var lease = await AcquireLeaseAsync();
        var budget = new ResourceBudget(1024 * 1024);
        await using var persistence = CreatePersistence(
            cache.Path,
            new TestCloudObjectStore(),
            lease,
            budget);

        await Assert.ThrowsAsync<PantsCorruptionException>(() =>
            persistence.ValidateWriteAuthorityAsync(CancellationToken.None).AsTask());

        Assert.Equal(0, budget.Current);
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

    static ProviderCloudPersistence CreatePersistence(
        string root,
        ICloudObjectStore controlStore,
        CloudLeaseCoordinator lease,
        ResourceBudget budget) =>
        new(
            root,
            new TestCloudObjectStore(),
            new TestCloudObjectStore(),
            controlStore,
            lease,
            walRetirementSettings: new WalRetirementSettings(
                budget,
                new MaintenanceMemoryGate(),
                WalRetirementSettings.DefaultPageBytes,
                WalRetirementSettings.DefaultQuantum,
                TimeProvider.System));
}
