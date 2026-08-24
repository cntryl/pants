using System.Net;

namespace Cntryl.Pants.Tests.Cloud;

public sealed class CloudObjectStoreFactoryTests
{
    [Fact]
    public void ShouldUseEndpointCompatibleStorageHttpDefaults()
    {
        using var handler = CloudObjectStoreFactory.CreateStorageHandler();

        Assert.Equal(Timeout.InfiniteTimeSpan, handler.ConnectTimeout);
        Assert.False(handler.UseCookies);
        Assert.Equal(64, handler.MaxConnectionsPerServer);
        Assert.Equal(HttpVersion.Version11, CloudObjectStoreFactory.StorageHttpClient.DefaultRequestVersion);
        Assert.Equal(
            HttpVersionPolicy.RequestVersionOrLower,
            CloudObjectStoreFactory.StorageHttpClient.DefaultVersionPolicy);
        Assert.Equal(Timeout.InfiniteTimeSpan, CloudObjectStoreFactory.StorageHttpClient.Timeout);
    }

    [Fact]
    public void ShouldUseShortIndependentCredentialConnectTimeout()
    {
        using var storageHandler = CloudObjectStoreFactory.CreateStorageHandler();
        using var credentialHandler = CloudObjectStoreFactory.CreateCredentialHandler();

        Assert.Equal(Timeout.InfiniteTimeSpan, storageHandler.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), credentialHandler.ConnectTimeout);
        Assert.NotSame(
            CloudObjectStoreFactory.StorageHttpClient,
            CloudObjectStoreFactory.CredentialHttpClient);
    }
}
