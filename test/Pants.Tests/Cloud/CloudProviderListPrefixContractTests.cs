using System.Net;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudProviderListPrefixContractTests
{
    [Theory]
    [InlineData("s3")]
    [InlineData("azure")]
    [InlineData("gcs-xml")]
    [InlineData("gcs-json")]
    public async Task ShouldRejectForeignObjectInProviderListPage(string provider)
    {
        const string foreignKey = "other/sst/foreign.sst";
        using var handler = new ListResponseHandler(provider, foreignKey);
        using var client = new HttpClient(handler);
        await using var store = await CloudProviderTestFactory.OpenAsync(
            provider,
            client,
            prefix: "tenant");

        var exception = await Assert.ThrowsAsync<PantsIOException>(() =>
            store.ListPageAsync("sst/", null, CancellationToken.None).AsTask());

        Assert.Contains(foreignKey, exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Requests);
    }

    sealed class ListResponseHandler(string provider, string foreignKey) : HttpMessageHandler
    {
        int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            var body = provider switch
            {
                "s3" => $"""
                    <ListBucketResult>
                      <Contents><Key>tenant/sst/valid.sst</Key></Contents>
                      <Contents><Key>{foreignKey}</Key></Contents>
                      <IsTruncated>false</IsTruncated>
                    </ListBucketResult>
                    """,
                "azure" => $"""
                    <EnumerationResults>
                      <Blobs>
                        <Blob><Name>tenant/sst/valid.sst</Name></Blob>
                        <Blob><Name>{foreignKey}</Name></Blob>
                      </Blobs>
                    </EnumerationResults>
                    """,
                "gcs-xml" => $"""
                    <ListBucketResult>
                      <Contents><Key>tenant/sst/valid.sst</Key></Contents>
                      <Contents><Key>{foreignKey}</Key></Contents>
                      <IsTruncated>false</IsTruncated>
                    </ListBucketResult>
                    """,
                "gcs-json" => $$"""
                    {
                      "items": [
                        { "name": "tenant/sst/valid.sst" },
                        { "name": "{{foreignKey}}" }
                      ]
                    }
                    """,
                _ => throw new InvalidOperationException($"Unknown provider '{provider}'.")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }
}
