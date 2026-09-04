namespace Cntryl.Pants.Tests;

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
    public async Task DatabaseRejectsColumnFamilyHandleOwnedByAnotherInstance()
    {
        await using var first = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var second = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());

        var exception = await Assert.ThrowsAsync<PantsInvalidArgumentException>(() => second.BeginTransactionAsync(
            first.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly).AsTask());

        Assert.Equal(PantsErrorCode.InvalidArgument, exception.Code);
    }
}
