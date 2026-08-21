namespace Pants.Tests;

public sealed class PantsPublicApiTests
{
    [Fact]
    public void ShouldExposePublicSurfaceThroughInterfacesAndImmutableContracts()
    {
        Type[] publicTypes = typeof(PantsDatabase).Assembly
            .GetExportedTypes();

        Assert.Contains(typeof(IPantsDatabase), publicTypes);
        Assert.Contains(typeof(IPantsTransaction), publicTypes);
        Assert.Contains(typeof(IPantsScan), publicTypes);
        Assert.Contains(typeof(IPantsColumnFamily), publicTypes);
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
        Type returnType = typeof(PantsDatabase)
            .GetMethod(nameof(PantsDatabase.OpenAsync))!
            .ReturnType;

        Assert.Equal(typeof(ValueTask<IPantsDatabase>), returnType);
        Assert.True(typeof(PantsDatabase).IsAbstract && typeof(PantsDatabase).IsSealed);
    }

    [Fact]
    public async Task DatabaseRejectsColumnFamilyHandleOwnedByAnotherInstance()
    {
        await using IPantsDatabase first = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsDatabase second = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());

        PantsInvalidArgumentException exception = await Assert.ThrowsAsync<PantsInvalidArgumentException>(
            () => second.BeginTransactionAsync(
                first.DefaultColumnFamily,
                PantsTransactionMode.ReadOnly).AsTask());

        Assert.Equal(PantsErrorCode.InvalidArgument, exception.Code);
    }
}
