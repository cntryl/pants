namespace Pants.Tests;

public sealed class PantsPublicApiTests
{
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
