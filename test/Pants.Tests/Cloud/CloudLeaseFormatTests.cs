using System.Text;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudLeaseFormatTests
{
    [Fact]
    public async Task ShouldWriteAndReadStrictVersionFourLease()
    {
        var store = new TestCloudObjectStore();
        var leases = new CloudObjectLeaseStore(store, PantsCloudObjectLayout.LeaseObjectKey);
        var record = new CloudLeaseRecord("writer", 7, "owner", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(10));

        Assert.True(await leases.TryCreateAsync(record, CancellationToken.None));
        var roundtrip = await leases.ReadAsync(CancellationToken.None);

        Assert.NotNull(roundtrip);
        Assert.Equal(record, roundtrip.Lease);
        Assert.StartsWith("lease_version: 4\nfencing_epoch: 7\nholder: writer\nowner: owner\nacquired: ", Encoding.UTF8.GetString(store.Data.Span), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lease_version: 4\nfencing_epoch: 1\nholder: w\nowner: o\nacquired: 1970-01-01T00:00:00Z\nexpires: 1970-01-01T00:00:10Z")]
    [InlineData("lease_version: 4\nfencing_epoch: 1\nholder: w\nowner: o\nacquired: 1970-01-01T00:00:00Z\nexpires: 1970-01-01T00:00:10Z\nholder: other\n")]
    [InlineData("lease_version: 4\nfencing_epoch: 1\nholder: w\nowner: o\nacquired: 1970-01-01T00:00:00Z\nexpires: 1970-01-01T00:00:10Z\nmetadata: legacy\n")]
    [InlineData("lease_version: 4\nfencing_epoch: 0\nholder: w\nowner: o\nacquired: 1970-01-01T00:00:00Z\nexpires: 1970-01-01T00:00:10Z\n")]
    public async Task ShouldRejectMalformedVersionFourLease(string text)
    {
        var store = new TestCloudObjectStore();
        store.Seed(PantsCloudObjectLayout.LeaseObjectKey, Encoding.UTF8.GetBytes(text));
        var leases = new CloudObjectLeaseStore(store, PantsCloudObjectLayout.LeaseObjectKey);

        await Assert.ThrowsAsync<PantsLeaseIndeterminateException>(async () => await leases.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("2026-11-03")]
    [InlineData("11/03/2026 12:00:00 +00:00")]
    [InlineData("2026-11-03T12:00:00")]
    [InlineData("2026-11-03T12:00:00Z ")]
    [InlineData("2026-11-03T12:00:00+0530")]
    [InlineData("2026-11-03T12:00:00+24:00")]
    [InlineData("2026-11-03T12:00:00+01:60")]
    public async Task ShouldRejectLeaseTimestampsOutsideRfc3339(string timestamp)
    {
        foreach (var field in new[] { "acquired", "expires" })
        {
            var acquired = field == "acquired" ? timestamp : "2026-11-03T12:00:00Z";
            var expires = field == "expires" ? timestamp : "2026-11-03T12:00:10Z";
            var document = $"lease_version: 4\nfencing_epoch: 7\nholder: writer\nowner: owner\nacquired: {acquired}\nexpires: {expires}\n";
            var store = new TestCloudObjectStore();
            store.Seed(PantsCloudObjectLayout.LeaseObjectKey, Encoding.UTF8.GetBytes(document));
            var leases = new CloudObjectLeaseStore(store, PantsCloudObjectLayout.LeaseObjectKey);

            await Assert.ThrowsAsync<PantsLeaseIndeterminateException>(async () => await leases.ReadAsync(CancellationToken.None));
        }
    }

    [Theory]
    [InlineData("2026-11-03T12:00:00Z")]
    [InlineData("2026-11-03t12:00:00z")]
    [InlineData("2026-11-03T12:00:00.123456789Z")]
    [InlineData("2026-11-03T12:00:00.123456789+05:30")]
    [InlineData("2026-11-03t12:00:00-04:00")]
    [InlineData("2026-11-03T12:00:00+23:59")]
    [InlineData("2026-11-03T12:00:00-23:59")]
    [InlineData("2026-11-03 12:00:00Z")]
    [InlineData("2026-11-03T12:00:00.123456789123Z")]
    [InlineData("2016-12-31T23:59:60Z")]
    [InlineData("2016-12-31T23:59:60.123456789Z")]
    public async Task ShouldAcceptRfc3339LeaseTimestampCaseAndFractionalPrecision(string timestamp)
    {
        var document = $"lease_version: 4\nfencing_epoch: 7\nholder: writer\nowner: owner\nacquired: {timestamp}\nexpires: {timestamp}\n";
        var store = new TestCloudObjectStore();
        store.Seed(PantsCloudObjectLayout.LeaseObjectKey, Encoding.UTF8.GetBytes(document));
        var leases = new CloudObjectLeaseStore(store, PantsCloudObjectLayout.LeaseObjectKey);

        var parsed = await leases.ReadAsync(CancellationToken.None);

        Assert.NotNull(parsed);
        Assert.Equal(7UL, parsed.Lease.Epoch);
        Assert.Equal(parsed.Lease.AcquiredAtUtc, parsed.Lease.ExpiresAtUtc);
    }

    [Theory]
    [InlineData("2026-11-03T12:00:00+05:30", "2026-11-03T06:30:00Z")]
    [InlineData("2026-11-03T12:00:00-23:59", "2026-11-04T11:59:00Z")]
    [InlineData("2016-12-31T23:59:60Z", "2017-01-01T00:00:00Z")]
    public async Task ShouldPreserveLeaseExpiryInstantWhenNormalizingRfc3339(string timestamp, string expectedUtc)
    {
        var document = $"lease_version: 4\nfencing_epoch: 7\nholder: writer\nowner: owner\nacquired: {timestamp}\nexpires: {timestamp}\n";
        var store = new TestCloudObjectStore();
        store.Seed(PantsCloudObjectLayout.LeaseObjectKey, Encoding.UTF8.GetBytes(document));
        var leases = new CloudObjectLeaseStore(store, PantsCloudObjectLayout.LeaseObjectKey);

        var parsed = await leases.ReadAsync(CancellationToken.None);

        Assert.NotNull(parsed);
        Assert.Equal(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), parsed.Lease.ExpiresAtUtc);
    }

    [Fact]
    public async Task ShouldFailClosedWhenInitializedCloudMetadataLosesLease()
    {
        var store = new CountingCloudObjectStore();
        await store.PutAsync(CloudMetadataAuthority.SentinelKey, "midge cloud metadata authority v3 initialized\n"u8.ToArray(),
            new PantsCloudObjectWriteCondition.IfAbsent(), CancellationToken.None);
        var leases = new CloudObjectLeaseStore(store, PantsCloudObjectLayout.LeaseObjectKey);

        await Assert.ThrowsAsync<PantsCorruptionException>(async () => await leases.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ShouldRejectLegacyLeaseWithoutRewritingIt()
    {
        var store = new TestCloudObjectStore();
        var bytes = "epoch: 1\nholder_id: old\nowner_token: old\nacquired_at: 1970-01-01T00:00:00Z\nexpires_at: 1970-01-01T00:00:10Z\n"u8.ToArray();
        store.Seed(PantsCloudObjectLayout.LeaseObjectKey, bytes);
        var leases = new CloudObjectLeaseStore(store, PantsCloudObjectLayout.LeaseObjectKey);

        await Assert.ThrowsAsync<PantsLeaseIndeterminateException>(async () => await leases.ReadAsync(CancellationToken.None));
        Assert.Equal(bytes, store.Data.ToArray());
        Assert.Equal(0, store.PutCount);
    }
}
