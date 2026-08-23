namespace Cntryl.Pants.Tests;

public sealed class CloudLeaseCoordinatorTests
{
    [Fact]
    public async Task ShouldFenceFormerHolderGivenLeaseTakeoverAfterGracePeriod()
    {
        var store = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var leaseLosses = 0;
        using var first = new CloudLeaseCoordinator(
            store,
            clock,
            "first",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1),
            () => leaseLosses++);
        using var second = new CloudLeaseCoordinator(
            store,
            clock,
            "second",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1));
        Assert.Equal(1UL, await first.AcquireAsync(CancellationToken.None));

        await Assert.ThrowsAsync<PantsLeaseHeldException>(
            () => second.AcquireAsync(CancellationToken.None).AsTask());
        clock.UtcNow += TimeSpan.FromSeconds(12);
        Assert.Equal(2UL, await second.AcquireAsync(CancellationToken.None));

        Assert.Throws<PantsFencedException>(first.EnsureValid);
        Assert.Equal(1, leaseLosses);
        Assert.Throws<PantsFencedException>(first.EnsureValid);
        Assert.Equal(1, leaseLosses);
    }

    [Fact]
    public async Task ShouldSurfaceIndeterminateRenewalAndFailClosed()
    {
        var store = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var leaseLosses = 0;
        using var lease = new CloudLeaseCoordinator(
            store,
            clock,
            "holder",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1),
            () => leaseLosses++);
        await lease.AcquireAsync(CancellationToken.None);
        store.IndeterminateRead = true;

        await Assert.ThrowsAsync<PantsLeaseIndeterminateException>(
            () => lease.RenewAsync(CancellationToken.None).AsTask());

        Assert.False(lease.IsHealthy);
        Assert.Throws<PantsFencedException>(lease.EnsureValid);
        Assert.Equal(1, leaseLosses);
    }

    [Fact]
    public async Task ShouldFenceBeforeRenewalWriteGivenReadCrossesMonotonicDeadline()
    {
        var store = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var leaseLosses = 0;
        using var lease = new CloudLeaseCoordinator(
            store,
            clock,
            "holder",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1),
            () => leaseLosses++);
        await lease.AcquireAsync(CancellationToken.None);
        clock.UtcNow += TimeSpan.FromSeconds(5);
        store.AfterNextRead = () => clock.UtcNow = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(10);

        await Assert.ThrowsAsync<PantsFencedException>(
            () => lease.RenewAsync(CancellationToken.None).AsTask());

        Assert.Equal(0, store.ReplaceAttempts);
        Assert.False(lease.IsHealthy);
        Assert.Equal(1, leaseLosses);
    }

    [Fact]
    public async Task ShouldNeverResurrectAuthorityGivenRenewalCompletesAfterDeadline()
    {
        var store = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var leaseLosses = 0;
        var renewalStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRenewal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var lease = new CloudLeaseCoordinator(
            store,
            clock,
            "holder",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1),
            () => leaseLosses++);
        await lease.AcquireAsync(CancellationToken.None);
        clock.UtcNow += TimeSpan.FromSeconds(5);
        store.BeforeNextReplaceAsync = async cancellationToken =>
        {
            renewalStarted.SetResult();
            await allowRenewal.Task.WaitAsync(cancellationToken);
        };

        var renewal = lease.RenewAsync(CancellationToken.None).AsTask();
        await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.UtcNow = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(10);
        Assert.False(lease.IsHealthy);
        allowRenewal.SetResult();

        await Assert.ThrowsAsync<PantsFencedException>(
            () => renewal);

        Assert.NotNull(store.Lease);
        Assert.True(store.Lease.ExpiresAtUtc + TimeSpan.FromSeconds(1) < clock.UtcNow);
        Assert.Equal(2, store.ReplaceAttempts);
        Assert.False(lease.IsHealthy);
        Assert.Equal(1, leaseLosses);
        Assert.Throws<PantsFencedException>(lease.EnsureValid);
        await Assert.ThrowsAsync<PantsFencedException>(
            () => lease.RenewAsync(CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<PantsFencedException>(
            () => lease.AcquireAsync(CancellationToken.None).AsTask());
        Assert.Equal(2, store.ReplaceAttempts);
        Assert.Equal(1, leaseLosses);
    }

    [Fact]
    public async Task ShouldRemainHealthyPastPreviousDeadlineGivenTimelyRenewal()
    {
        var store = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var lease = new CloudLeaseCoordinator(
            store,
            clock,
            "holder",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1));
        await lease.AcquireAsync(CancellationToken.None);
        clock.UtcNow += TimeSpan.FromSeconds(5);
        store.AfterNextReplace = () =>
            clock.UtcNow = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(9);

        await lease.RenewAsync(CancellationToken.None);
        clock.UtcNow = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(10);

        Assert.True(lease.IsHealthy);
        Assert.Equal(DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(15), store.Lease?.ExpiresAtUtc);
    }

    [Fact]
    public async Task ShouldConfirmIndeterminateRenewalGivenMatchingReadbackBeforeDeadline()
    {
        var store = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var leaseLosses = 0;
        using var lease = new CloudLeaseCoordinator(
            store,
            clock,
            "holder",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1),
            () => leaseLosses++);
        await lease.AcquireAsync(CancellationToken.None);
        clock.UtcNow += TimeSpan.FromSeconds(5);
        store.ApplyIndeterminateReplace = true;
        store.IndeterminateReplace = true;

        await lease.RenewAsync(CancellationToken.None);

        Assert.True(lease.IsHealthy);
        Assert.Equal(DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(15), store.Lease?.ExpiresAtUtc);
        Assert.Equal(1, store.ReplaceAttempts);
        Assert.Equal(0, leaseLosses);
    }

    [Fact]
    public async Task ShouldSurfaceUnavailableRenewalGivenMismatchedReadback()
    {
        var store = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var leaseLosses = 0;
        using var lease = new CloudLeaseCoordinator(
            store,
            clock,
            "holder",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1),
            () => leaseLosses++);
        await lease.AcquireAsync(CancellationToken.None);
        clock.UtcNow += TimeSpan.FromSeconds(5);
        store.IndeterminateReplace = true;

        await Assert.ThrowsAsync<PantsLeaseUnavailableException>(
            () => lease.RenewAsync(CancellationToken.None).AsTask());

        Assert.False(lease.IsHealthy);
        Assert.Equal(1, store.ReplaceAttempts);
        Assert.Equal(1, leaseLosses);
    }

    [Fact]
    public async Task ShouldSurfaceIndeterminateRenewalGivenReadbackFailure()
    {
        var store = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var leaseLosses = 0;
        using var lease = new CloudLeaseCoordinator(
            store,
            clock,
            "holder",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1),
            () => leaseLosses++);
        await lease.AcquireAsync(CancellationToken.None);
        clock.UtcNow += TimeSpan.FromSeconds(5);
        store.IndeterminateReplace = true;
        store.AfterNextReplace = () => store.IndeterminateRead = true;

        await Assert.ThrowsAsync<PantsLeaseIndeterminateException>(
            () => lease.RenewAsync(CancellationToken.None).AsTask());

        Assert.False(lease.IsHealthy);
        Assert.Equal(1, store.ReplaceAttempts);
        Assert.Equal(1, leaseLosses);
    }

    [Fact]
    public async Task ShouldNotifyLeaseLossExactlyOnceGivenRepeatedExpiredObservations()
    {
        var store = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var leaseLosses = 0;
        using var lease = new CloudLeaseCoordinator(
            store,
            clock,
            "holder",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1),
            () => leaseLosses++);
        await lease.AcquireAsync(CancellationToken.None);
        clock.UtcNow += TimeSpan.FromSeconds(10);

        Assert.False(lease.IsHealthy);
        clock.UtcNow = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(1);
        for (var observation = 0; observation < 10; observation++)
        {
            Assert.False(lease.IsHealthy);
        }

        Assert.Equal(1, leaseLosses);
    }

    [Fact]
    public async Task ShouldPersistMidgeLeaseDocumentUsingConditionalObjectWrites()
    {
        var objects = new TestCloudObjectStore();
        var store = new CloudObjectLeaseStore(objects, PantsCloudObjectLayout.LeaseObjectKey);
        var clock = new ManualClock(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        using var lease = new CloudLeaseCoordinator(
            store,
            clock,
            "holder@host",
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(1));

        Assert.Equal(1UL, await lease.AcquireAsync(CancellationToken.None));
        var document = System.Text.Encoding.UTF8.GetString(objects.Data.Span);
        Assert.Contains("epoch: 1\n", document, StringComparison.Ordinal);
        Assert.Contains("holder_id: holder@host\n", document, StringComparison.Ordinal);
        Assert.Contains("owner_token: ", document, StringComparison.Ordinal);
        Assert.Contains("acquired_at: 2026-08-21T12:00:00.0000000Z\n", document, StringComparison.Ordinal);
        Assert.Contains("expires_at: 2026-08-21T12:00:30.0000000Z\n", document, StringComparison.Ordinal);
        Assert.IsType<CloudObjectWriteCondition.IfAbsent>(objects.LastCondition);

        clock.UtcNow += TimeSpan.FromSeconds(5);
        await lease.RenewAsync(CancellationToken.None);
        Assert.IsType<CloudObjectWriteCondition.IfVersion>(objects.LastCondition);
    }
}
