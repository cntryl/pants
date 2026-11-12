using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudNamespaceNormalizationTests
{
    [Theory]
    [InlineData("/database/", "database", false)]
    [InlineData("///database///", "database", false)]
    [InlineData("/", "", false)]
    [InlineData("/database/", "database", true)]
    [InlineData("///database///", "database", true)]
    [InlineData("/", "", true)]
    public async Task ShouldShareAuthorityAndRecoverExactRowsAcrossNormalizedPrefixAliases(
        string decorated,
        string canonical,
        bool copiedLocation)
    {
        using var originalCache = new TemporaryDirectory();
        using var successorCache = new TemporaryDirectory();
        using var contenderCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var provider = new PantsAzureBlobProvider(
            "account",
            "container",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.SasToken("sig=test"));
        var location = copiedLocation
            ? new PantsCloudStorageLocation(provider, "unused") with { Prefix = decorated }
            : new PantsCloudStorageLocation(provider, decorated);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        var canonicalLocation = new PantsCloudStorageLocation(provider, canonical);

        await using (var original = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Cloud(originalCache.Path, location), dependencies))
        {
            Assert.Equal(canonical, location.Prefix);
            await using (var write = await original.Transactions.BeginAsync(
                             original.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
            {
                write.Put("retained"u8.ToArray(), "same-namespace"u8.ToArray());
                await write.CommitAsync(PantsWriteOptions.CloudStrict);
            }

            await original.Maintenance.FlushAsync(original.ColumnFamilies.DefaultFamily);
            Assert.True((await original.Diagnostics.GetRuntimeMetricsAsync()).SstCount > 0);
            await Assert.ThrowsAsync<PantsLeaseHeldException>(() => PantsDatabase.OpenForTestingAsync(
                PantsOpenOptions.Cloud(contenderCache.Path, canonicalLocation), dependencies).AsTask());
        }

        await using var successor = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(successorCache.Path, canonicalLocation), dependencies);
        await using var read = await successor.Transactions.BeginAsync(
            successor.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("same-namespace", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
            await read.GetAsync("retained"u8.ToArray()))));
    }

    [Theory]
    [InlineData("/database/../escape/")]
    [InlineData("/./database/")]
    public void ShouldStillRejectActualDotSegmentsAfterTrimmingNamespaceSeparators(string prefix)
    {
        using var directory = new TemporaryDirectory();
        var location = new PantsCloudStorageLocation(
            new PantsAzureBlobProvider("account", "container", new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=test")), prefix);

        Assert.Throws<PantsInvalidArgumentException>(() => PantsOpenOptions.Cloud(directory.Path, location));

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }
}
