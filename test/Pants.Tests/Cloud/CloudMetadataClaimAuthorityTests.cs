using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudMetadataClaimAuthorityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShouldRejectEqualEpochPointerWithDifferentLeaseHolderOrOwner(bool differentHolder)
    {
        var store = new CountingCloudObjectStore();
        using var lease = new CloudLeaseCoordinator(new TestCloudLeaseStore(), new ManualClock(DateTimeOffset.UnixEpoch),
            "writer", TimeSpan.FromSeconds(10), TimeSpan.Zero);
        await lease.AcquireAsync(CancellationToken.None);
        var pointer = MetadataFrameCodec.Encode(4, JsonSerializer.SerializeToElement(new
        {
            writer_epoch = lease.Epoch,
            holder_id = differentHolder ? "other-holder" : lease.HolderId,
            owner_token = differentHolder ? lease.OwnerToken : "other-owner",
            committed = (object?)null
        }));
        await store.PutAsync(CloudMetadataAuthority.PointerKey, pointer,
            new PantsCloudObjectWriteCondition.IfAbsent(), CancellationToken.None);
        await store.PutAsync(CloudMetadataAuthority.SentinelKey, "midge cloud metadata authority v3 initialized\n"u8.ToArray(),
            new PantsCloudObjectWriteCondition.IfAbsent(), CancellationToken.None);
        var puts = store.PutCount;
        using var authority = new CloudMetadataAuthority(store, lease);

        await Assert.ThrowsAsync<PantsFencedException>(() => authority.ClaimAsync(CancellationToken.None).AsTask());

        Assert.Equal(puts, store.PutCount);
        Assert.Equal(pointer, (await store.GetAsync(CloudMetadataAuthority.PointerKey, CancellationToken.None))!.Data.ToArray());
    }
}
