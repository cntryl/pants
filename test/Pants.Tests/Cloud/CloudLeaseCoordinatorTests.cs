using System.Text;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudLeaseCoordinatorTests
{
    [Theory]
    [InlineData(PantsErrorCode.Timeout)]
    [InlineData(PantsErrorCode.Io)]
    public async Task ShouldConfirmAppliedRenewalGivenProviderWriteOutcomeIsAmbiguous(
        PantsErrorCode errorCode)
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
        store.ApplyReplaceBeforeException = true;
        store.NextReplaceException = errorCode switch
        {
            PantsErrorCode.Timeout => new PantsTimeoutException("Timed out after the write landed."),
            PantsErrorCode.Io => new PantsIOException("The connection failed after the write landed."),
            _ => throw new InvalidOperationException()
        };

        await lease.RenewAsync(CancellationToken.None);

        Assert.True(lease.IsHealthy);
        Assert.Equal(DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(15), store.Lease?.ExpiresAtUtc);
    }

    [Fact]
    public async Task ShouldDrainRenewalBeforeDisposingMutationGate()
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
        var renewalStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRenewal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeNextReplaceAsync = async cancellationToken =>
        {
            renewalStarted.SetResult();
            await allowRenewal.Task.WaitAsync(cancellationToken);
        };
        var renewal = lease.RenewAsync(CancellationToken.None).AsTask();
        await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedRelease = lease.ReleaseAsync(CancellationToken.None).AsTask();

        lease.Dispose();
        allowRenewal.SetResult();

        await Assert.ThrowsAsync<PantsFencedException>(() => renewal);
        await queuedRelease.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ShouldLoseConditionalCreateRaceWithoutAdvancingEpoch()
    {
        var store = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var first = new CloudLeaseCoordinator(
            store,
            clock,
            "first",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        using var second = new CloudLeaseCoordinator(
            store,
            clock,
            "second",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        var firstCreateStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstCreate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeNextCreateAsync = async cancellationToken =>
        {
            firstCreateStarted.SetResult();
            await allowFirstCreate.Task.WaitAsync(cancellationToken);
        };
        var firstAcquire = first.AcquireAsync(CancellationToken.None).AsTask();
        await firstCreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1UL, await second.AcquireAsync(CancellationToken.None));
        allowFirstCreate.SetResult();

        await Assert.ThrowsAsync<PantsLeaseHeldException>(() => firstAcquire);
        Assert.Equal(0UL, first.Epoch);
        Assert.Equal("second", store.Lease?.HolderId);
    }

    [Fact]
    public async Task ShouldLoseStaleTakeoverCasRaceWithoutAdvancingEpoch()
    {
        var store = new TestCloudLeaseStore();
        store.Seed(new CloudLeaseRecord(
            "expired",
            1,
            "expired-token",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(10)));
        var clock = new ManualClock(DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(11));
        using var first = new CloudLeaseCoordinator(
            store,
            clock,
            "first",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        using var second = new CloudLeaseCoordinator(
            store,
            clock,
            "second",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        var firstReplaceStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstReplace = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeNextReplaceAsync = async cancellationToken =>
        {
            firstReplaceStarted.SetResult();
            await allowFirstReplace.Task.WaitAsync(cancellationToken);
        };
        var firstAcquire = first.AcquireAsync(CancellationToken.None).AsTask();
        await firstReplaceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2UL, await second.AcquireAsync(CancellationToken.None));
        allowFirstReplace.SetResult();

        await Assert.ThrowsAsync<PantsLeaseHeldException>(() => firstAcquire);
        Assert.Equal(0UL, first.Epoch);
        Assert.Equal("second", store.Lease?.HolderId);
        Assert.Equal(2UL, store.Lease?.Epoch);
    }

    [Fact]
    public async Task ShouldReleaseLeaseForImmediateTakeoverAndBecomeUnhealthy()
    {
        var store = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var first = new CloudLeaseCoordinator(
            store,
            clock,
            "first",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1));
        using var second = new CloudLeaseCoordinator(
            store,
            clock,
            "second",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1));
        Assert.Equal(1UL, await first.AcquireAsync(CancellationToken.None));

        await first.ReleaseAsync(CancellationToken.None);

        Assert.False(first.IsHealthy);
        Assert.Equal(2UL, await second.AcquireAsync(CancellationToken.None));
        var replaceAttempts = store.ReplaceAttempts;
        await first.ReleaseAsync(CancellationToken.None);
        Assert.Equal(replaceAttempts, store.ReplaceAttempts);
    }

    [Fact]
    public async Task ShouldNotOverwriteSuccessorGivenReleaseObservesSupersededLease()
    {
        var store = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var lease = new CloudLeaseCoordinator(
            store,
            clock,
            "first",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        var epoch = await lease.AcquireAsync(CancellationToken.None);
        var successor = new CloudLeaseRecord(
            "second",
            epoch + 1,
            "successor-token",
            clock.UtcNow,
            clock.UtcNow + TimeSpan.FromSeconds(10));
        store.Seed(successor);

        await lease.ReleaseAsync(CancellationToken.None);

        Assert.False(lease.IsHealthy);
        Assert.Equal(successor, store.Lease);
    }

    [Fact]
    public async Task ShouldFenceRenewalGivenOnlyOwnerTokenWasForged()
    {
        var store = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var lease = new CloudLeaseCoordinator(
            store,
            clock,
            "holder",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        await lease.AcquireAsync(CancellationToken.None);
        store.Seed(Assert.IsType<CloudLeaseRecord>(store.Lease) with
        {
            OwnerToken = "forged-token"
        });

        await Assert.ThrowsAsync<PantsFencedException>(() =>
            lease.RenewAsync(CancellationToken.None).AsTask());

        Assert.False(lease.IsHealthy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("epoch: 1\nholder_id: holder\nowner_token: token\nacquired_at: 2026-08-21T12:00:00.0000000Z\n")]
    [InlineData(
        "epoch: 0\nholder_id: holder\nowner_token: token\nacquired_at: 2026-08-21T12:00:00.0000000Z\nexpires_at: 2026-08-21T12:00:30.0000000Z\n")]
    [InlineData(
        "epoch: nope\nholder_id: holder\nowner_token: token\nacquired_at: 2026-08-21T12:00:00.0000000Z\nexpires_at: 2026-08-21T12:00:30.0000000Z\n")]
    [InlineData(
        "epoch: 1\nholder_id: holder\nholder_id: duplicate\nowner_token: token\nacquired_at: 2026-08-21T12:00:00.0000000Z\nexpires_at: 2026-08-21T12:00:30.0000000Z\n")]
    public async Task ShouldRejectMalformedCloudLeaseDocument(string document)
    {
        var objects = new TestCloudObjectStore();
        objects.Seed(PantsCloudObjectLayout.LeaseObjectKey, Encoding.UTF8.GetBytes(document));
        var store = new CloudObjectLeaseStore(objects, PantsCloudObjectLayout.LeaseObjectKey);

        await Assert.ThrowsAsync<PantsCorruptionException>(() =>
            store.ReadAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ShouldDistinguishAbsentCloudLeaseFromMalformedDocument()
    {
        var store = new CloudObjectLeaseStore(
            new TestCloudObjectStore(),
            PantsCloudObjectLayout.LeaseObjectKey);

        Assert.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ShouldRejectNonUtf8CloudLeaseDocument()
    {
        var objects = new TestCloudObjectStore();
        var bytes = Encoding.UTF8.GetBytes("epoch: 1\nholder_id: ")
            .Concat(new byte[] { 0xff })
            .Concat(Encoding.UTF8.GetBytes(
                "\nowner_token: token\nacquired_at: 2026-08-21T12:00:00.0000000Z\nexpires_at: 2026-08-21T12:00:30.0000000Z\n"))
            .ToArray();
        objects.Seed(PantsCloudObjectLayout.LeaseObjectKey, bytes);
        var store = new CloudObjectLeaseStore(objects, PantsCloudObjectLayout.LeaseObjectKey);

        await Assert.ThrowsAsync<PantsCorruptionException>(() =>
            store.ReadAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ShouldEnforceExactTakeoverAndHealthExpiryBoundaries()
    {
        var store = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var first = new CloudLeaseCoordinator(
            store,
            clock,
            "first",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1));
        using var second = new CloudLeaseCoordinator(
            store,
            clock,
            "second",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1));
        await first.AcquireAsync(CancellationToken.None);
        clock.UtcNow += TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1);
        Assert.True(first.IsHealthy);
        clock.UtcNow += TimeSpan.FromTicks(1);
        Assert.False(first.IsHealthy);
        clock.UtcNow += TimeSpan.FromSeconds(1);

        await Assert.ThrowsAsync<PantsLeaseHeldException>(() =>
            second.AcquireAsync(CancellationToken.None).AsTask());
        clock.UtcNow += TimeSpan.FromTicks(1);
        Assert.Equal(2UL, await second.AcquireAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ShouldSerializeConcurrentObjectStoreAcquisitionRace()
    {
        var objects = new TestCloudObjectStore();
        var store = new CloudObjectLeaseStore(objects, PantsCloudObjectLayout.LeaseObjectKey);
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var first = new CloudLeaseCoordinator(
            store,
            clock,
            "first",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        using var second = new CloudLeaseCoordinator(
            store,
            clock,
            "second",
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero);
        var firstPutStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstPut = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        objects.BeforeNextPutAsync = async cancellationToken =>
        {
            firstPutStarted.SetResult();
            await allowFirstPut.Task.WaitAsync(cancellationToken);
        };
        var firstAcquire = first.AcquireAsync(CancellationToken.None).AsTask();
        await firstPutStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1UL, await second.AcquireAsync(CancellationToken.None));
        allowFirstPut.SetResult();

        await Assert.ThrowsAsync<PantsLeaseHeldException>(() => firstAcquire);
        var persisted = Assert.IsType<CloudLeaseSnapshot>(
            await store.ReadAsync(CancellationToken.None));
        Assert.Equal("second", persisted.Lease.HolderId);
        Assert.Equal(1UL, persisted.Lease.Epoch);
        Assert.Equal(0UL, first.Epoch);
    }

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

        await Assert.ThrowsAsync<PantsLeaseHeldException>(() => second.AcquireAsync(CancellationToken.None).AsTask());
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

        await Assert.ThrowsAsync<PantsLeaseIndeterminateException>(() =>
            lease.RenewAsync(CancellationToken.None).AsTask());

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

        await Assert.ThrowsAsync<PantsFencedException>(() => lease.RenewAsync(CancellationToken.None).AsTask());

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

        await Assert.ThrowsAsync<PantsFencedException>(() => renewal);

        Assert.NotNull(store.Lease);
        Assert.True(store.Lease.ExpiresAtUtc + TimeSpan.FromSeconds(1) < clock.UtcNow);
        Assert.Equal(2, store.ReplaceAttempts);
        Assert.False(lease.IsHealthy);
        Assert.Equal(1, leaseLosses);
        Assert.Throws<PantsFencedException>(lease.EnsureValid);
        await Assert.ThrowsAsync<PantsFencedException>(() => lease.RenewAsync(CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<PantsFencedException>(() => lease.AcquireAsync(CancellationToken.None).AsTask());
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

        await Assert.ThrowsAsync<PantsLeaseUnavailableException>(() =>
            lease.RenewAsync(CancellationToken.None).AsTask());

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

        await Assert.ThrowsAsync<PantsLeaseIndeterminateException>(() =>
            lease.RenewAsync(CancellationToken.None).AsTask());

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
        var document = Encoding.UTF8.GetString(objects.Data.Span);
        Assert.Contains("epoch: 1\n", document, StringComparison.Ordinal);
        Assert.Contains("holder_id: holder@host\n", document, StringComparison.Ordinal);
        Assert.Contains("owner_token: ", document, StringComparison.Ordinal);
        Assert.Contains("acquired_at: 2026-08-21T12:00:00.0000000Z\n", document, StringComparison.Ordinal);
        Assert.Contains("expires_at: 2026-08-21T12:00:30.0000000Z\n", document, StringComparison.Ordinal);
        Assert.IsType<PantsCloudObjectWriteCondition.IfAbsent>(objects.LastCondition);

        clock.UtcNow += TimeSpan.FromSeconds(5);
        await lease.RenewAsync(CancellationToken.None);
        Assert.IsType<PantsCloudObjectWriteCondition.IfVersion>(objects.LastCondition);
    }
}
