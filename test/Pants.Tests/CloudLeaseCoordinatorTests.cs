namespace Pants.Tests;

public sealed class CloudLeaseCoordinatorTests
{
    [Fact]
    public async Task ShouldFenceFormerHolderGivenLeaseTakeoverAfterGracePeriod()
    {
        var store = new TestCloudLeaseStore();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        int leaseLosses = 0;
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
        using var lease = new CloudLeaseCoordinator(
            store,
            clock,
            "holder",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1));
        await lease.AcquireAsync(CancellationToken.None);
        store.IndeterminateRead = true;

        await Assert.ThrowsAsync<PantsLeaseIndeterminateException>(
            () => lease.RenewAsync(CancellationToken.None).AsTask());

        Assert.False(lease.IsHealthy);
        Assert.Throws<PantsFencedException>(lease.EnsureValid);
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
        string document = System.Text.Encoding.UTF8.GetString(objects.Data.Span);
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

    private sealed class TestCloudLeaseStore : ICloudLeaseStore
    {
        private CloudLeaseRecord? _lease;
        private int _version;

        public bool IndeterminateRead { get; set; }

        public ValueTask<CloudLeaseSnapshot?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IndeterminateRead)
            {
                throw new PantsLeaseIndeterminateException(
                    "The conditional lease read outcome is unknown.");
            }

            CloudLeaseSnapshot? snapshot = _lease is null
                ? null
                : new CloudLeaseSnapshot(_lease, _version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask<bool> TryCreateAsync(
            CloudLeaseRecord lease,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_lease is not null)
            {
                return ValueTask.FromResult(false);
            }

            _lease = lease;
            _version++;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> TryReplaceAsync(
            string expectedVersion,
            CloudLeaseRecord lease,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StringComparer.Ordinal.Equals(
                    expectedVersion,
                    _version.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                return ValueTask.FromResult(false);
            }

            _lease = lease;
            _version++;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class TestCloudObjectStore : ICloudObjectStore
    {
        private string? _version;

        public ReadOnlyMemory<byte> Data { get; private set; }

        public CloudObjectWriteCondition? LastCondition { get; private set; }

        public ValueTask<CloudObject?> GetAsync(
            string objectKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloudObject? value = _version is null ? null : new CloudObject(Data, _version);
            return ValueTask.FromResult(value);
        }

        public ValueTask<bool> PutAsync(
            string objectKey,
            ReadOnlyMemory<byte> data,
            CloudObjectWriteCondition condition,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCondition = condition;
            bool accepted = condition switch
            {
                CloudObjectWriteCondition.Unconditional => true,
                CloudObjectWriteCondition.IfAbsent => _version is null,
                CloudObjectWriteCondition.IfVersion expected =>
                    StringComparer.Ordinal.Equals(expected.Version, _version),
                _ => false
            };
            if (accepted)
            {
                Data = data.ToArray();
                _version = Guid.NewGuid().ToString("N");
            }

            return ValueTask.FromResult(accepted);
        }
    }
}
