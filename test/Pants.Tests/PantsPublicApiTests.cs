using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants;

public sealed class PantsPublicApiTests
{
    [Fact]
    public void ShouldExposePublicSurfaceThroughInterfacesAndImmutableContracts()
    {
        var publicTypes = typeof(IPantsDatabase).Assembly
            .GetExportedTypes();

        Assert.Equal("Cntryl.Pants.Abstractions", typeof(IPantsDatabase).Assembly.GetName().Name);
        Assert.Equal("Cntryl.Pants.Core", typeof(PantsDatabase).Assembly.GetName().Name);
        Assert.NotEqual(typeof(IPantsDatabase).Assembly, typeof(PantsDatabase).Assembly);
        Assert.Equal(typeof(IPantsDatabase).Assembly, typeof(PantsOpenOptions).Assembly);
        Assert.Equal(typeof(PantsDatabase).Assembly, typeof(PantsCloudPreflightExtensions).Assembly);
        Assert.DoesNotContain(
            typeof(IPantsDatabase).Assembly.GetReferencedAssemblies(),
            static assembly => assembly.Name == "Cntryl.Pants.Core");
        Assert.Contains(typeof(IPantsDatabase), publicTypes);
        Assert.Contains(typeof(IPantsTransaction), publicTypes);
        Assert.Contains(typeof(IPantsScan), publicTypes);
        Assert.Contains(typeof(IPantsColumnFamily), publicTypes);
        Assert.DoesNotContain(typeof(PantsDatabase), publicTypes);
        Assert.All(
            publicTypes.Where(static type => type.Name.StartsWith("IPants", StringComparison.Ordinal)),
            static type => Assert.True(type.IsInterface));
        Assert.All(
            publicTypes.Where(static type => type.IsClass && type.Name.EndsWith("Metrics", StringComparison.Ordinal)),
            static type => Assert.True(type.IsSealed));
    }

    [Fact]
    public void DatabaseFactoryReturnsPublicAbstraction()
    {
        var returnType = typeof(PantsDatabase)
            .GetMethod(nameof(PantsDatabase.OpenAsync))!
            .ReturnType;

        Assert.Equal(typeof(ValueTask<IPantsDatabase>), returnType);
        Assert.True(typeof(PantsDatabase).IsAbstract && typeof(PantsDatabase).IsSealed);
    }

    [Fact]
    public void PackagesKeepStableAssemblyIdentityAcrossMajorPackageRelease()
    {
        var expected = new Version(1, 0, 0, 0);

        Assert.Equal(expected, typeof(IPantsDatabase).Assembly.GetName().Version);
        Assert.Equal(expected, typeof(PantsDatabase).Assembly.GetName().Version);
        Assert.Equal(expected, typeof(PantsServiceCollectionExtensions).Assembly.GetName().Version);
    }

    [Fact]
    public void SourceAssembliesOwnAllDeclaredNamespaces()
    {
        var assemblies = new[]
        {
            typeof(IPantsDatabase).Assembly,
            typeof(PantsDatabase).Assembly,
            typeof(PantsServiceCollectionExtensions).Assembly
        };

        Assert.All(
            assemblies
                .SelectMany(static assembly => assembly.DefinedTypes)
                .Where(static type => type.Namespace is not null),
            static type => Assert.StartsWith("Cntryl.Pants", type.Namespace, StringComparison.Ordinal));
    }

    [Fact]
    public void DatabaseContractExposesFocusedCapabilityFacets()
    {
        var properties = typeof(IPantsDatabase)
            .GetProperties()
            .ToDictionary(static property => property.Name, static property => property.PropertyType);

        Assert.Equal(typeof(PantsOpenOptions), properties[nameof(IPantsDatabase.Options)]);
        Assert.Equal(
            typeof(PantsDatabaseCapabilities),
            properties[nameof(IPantsDatabase.Capabilities)]);
        Assert.Equal(
            typeof(IPantsColumnFamilyCatalog),
            properties[nameof(IPantsDatabase.ColumnFamilies)]);
        Assert.Equal(
            typeof(IPantsTransactionFactory),
            properties[nameof(IPantsDatabase.Transactions)]);
        Assert.Equal(
            typeof(IPantsDatabaseMaintenance),
            properties[nameof(IPantsDatabase.Maintenance)]);
        Assert.Equal(
            typeof(IPantsDatabaseDiagnostics),
            properties[nameof(IPantsDatabase.Diagnostics)]);
        Assert.Equal(
            typeof(IPantsPersistentStorage),
            properties[nameof(IPantsDatabase.PersistentStorage)]);
        Assert.Equal(typeof(IPantsCloudDatabase), properties[nameof(IPantsDatabase.Cloud)]);
        Assert.DoesNotContain(
            typeof(IPantsDatabase).GetMethods(),
            static method => method.Name is "BeginTransactionAsync" or
                "CreateColumnFamilyAsync" or
                "FlushAsync" or
                "VerifyStorageAsync");
    }

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
    public void OptionsExposeRawGroupsWhileCoreOwnsRuntimeValidation()
    {
        var options = PantsOpenOptions.Create(
            new PantsStorageConfiguration.InMemory(),
            PantsRuntimeConfiguration.Default,
            PantsMemoryConfiguration.Default,
            PantsLeaseConfiguration.Default);

        Assert.NotNull(options.Runtime);
        Assert.NotNull(options.Memory);
        Assert.NotNull(options.Lease);
        Assert.Equal(typeof(PantsDatabase).Assembly, typeof(PantsOpenOptionsValidator).Assembly);
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
