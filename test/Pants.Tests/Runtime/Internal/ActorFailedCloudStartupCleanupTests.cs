using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime.Internal;

public sealed class ActorFailedCloudStartupCleanupTests
{
    [Fact]
    public async Task OriginalStartupFailurePropagatesWhenCleanupDisposalAlsoThrows()
    {
        var throwingStore = new ThrowingCloudObjectStore();
        var objectStores = new ProviderObjectStoreSet(throwingStore, throwingStore, throwingStore);

        var propagated = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            try
            {
                throw new InvalidOperationException("original startup failure");
            }
            catch
            {
                await Actor.DisposeFailedCloudStartupResourcesAsync(null, objectStores);
                throw;
            }
        });

        Assert.Equal("original startup failure", propagated.Message);
    }
}
