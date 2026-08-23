namespace Cntryl.Pants.Tests;

public sealed class PantsPublicApiTests
{
    [Fact]
    public void ShouldExposePublicSurfaceThroughInterfacesAndImmutableContracts()
    {
        var publicTypes = typeof(PantsDatabase).Assembly
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
