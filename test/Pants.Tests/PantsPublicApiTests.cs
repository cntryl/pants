using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants;

public sealed class PantsPublicApiTests
{
    [Fact]
    public async Task DatabaseCapabilitiesMatchAvailableFacets()
    {
        using var localDirectory = new TemporaryDirectory();
        using var cloudDirectory = new TemporaryDirectory();
        await using var memory = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var local = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(localDirectory.Path));
        await using var cloud = await PantsDatabase.OpenAsync(PantsOpenOptions.SimulatedCloud(
            cloudDirectory.Path,
            "contracts",
            "capabilities"));

        Assert.False(memory.Capabilities.IsPersistent);
        Assert.False(memory.Capabilities.IsCloudBacked);
        Assert.Null(memory.PersistentStorage);
        Assert.Null(memory.Cloud);
        Assert.True(local.Capabilities.IsPersistent);
        Assert.False(local.Capabilities.IsCloudBacked);
        Assert.NotNull(local.PersistentStorage);
        Assert.Null(local.Cloud);
        Assert.True(cloud.Capabilities.IsPersistent);
        Assert.True(cloud.Capabilities.IsCloudBacked);
        Assert.NotNull(cloud.PersistentStorage);
        Assert.NotNull(cloud.Cloud);
    }

    [Fact]
    public void OptionsExposeRawGroupsForRuntimeValidation()
    {
        var options = PantsOpenOptions.Create(
            new PantsStorageConfiguration.InMemory(),
            PantsRuntimeConfiguration.Default,
            PantsMemoryConfiguration.Default,
            PantsLeaseConfiguration.Default);

        Assert.NotNull(options.Runtime);
        Assert.NotNull(options.Memory);
        Assert.NotNull(options.Lease);
        PantsOpenOptionsValidator.Validate(options);
    }

    [Fact]
    public async Task DatabaseRejectsColumnFamilyHandleOwnedByAnotherInstance()
    {
        await using var first = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var second = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());

        var exception = await Assert.ThrowsAsync<PantsInvalidArgumentException>(() => second.Transactions.BeginAsync(
            first.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly).AsTask());

        Assert.Equal(PantsErrorCode.InvalidArgument, exception.Code);
    }
}
