using Cntryl.Pants.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Pants.Tests;

public sealed class PantsDependencyInjectionTests
{
    [Fact]
    public async Task AddPantsRegistersOneLazyAsyncDatabaseProvider()
    {
        PantsOpenOptions options = PantsOpenOptions.InMemory();
        var services = new ServiceCollection();

        services.AddPants(options);

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();
        IPantsDatabaseProvider provider = serviceProvider.GetRequiredService<IPantsDatabaseProvider>();
        IPantsDatabase first = await provider.GetDatabaseAsync();
        IPantsDatabase second = await provider.GetDatabaseAsync();

        Assert.Same(first, second);
        Assert.Same(options, first.Options);
        Assert.Single(serviceProvider.GetServices<IPantsDatabaseProvider>());
        Assert.NotNull(serviceProvider.GetService<IPantsDatabaseFactory>());
        Assert.Null(serviceProvider.GetService<IPantsDatabase>());
    }

    [Fact]
    public async Task CallerCancellationDoesNotPoisonSharedInitialization()
    {
        var databaseFactory = new DelayedPantsDatabaseFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IPantsDatabaseFactory>(databaseFactory);
        services.AddPants(PantsOpenOptions.InMemory());

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();
        IPantsDatabaseProvider provider = serviceProvider.GetRequiredService<IPantsDatabaseProvider>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetDatabaseAsync(cancellation.Token).AsTask());

        databaseFactory.AllowOpen();
        IPantsDatabase database = await provider.GetDatabaseAsync();

        Assert.NotNull(database);
        Assert.Equal(1, databaseFactory.OpenCount);
    }

    [Fact]
    public async Task ContainerOwnsAndAsynchronouslyDisposesOpenedDatabase()
    {
        var services = new ServiceCollection();
        services.AddPants(PantsOpenOptions.InMemory());
        ServiceProvider serviceProvider = services.BuildServiceProvider();
        IPantsDatabaseProvider provider = serviceProvider.GetRequiredService<IPantsDatabaseProvider>();
        IPantsDatabase database = await provider.GetDatabaseAsync();

        await serviceProvider.DisposeAsync();

        await Assert.ThrowsAsync<PantsAbortedException>(
            () => database.GetRuntimeMetricsAsync().AsTask());
    }

    [Fact]
    public async Task AddKeyedPantsSupportsIndependentNamedDatabases()
    {
        var services = new ServiceCollection();
        services.AddKeyedPants("primary", PantsOpenOptions.InMemory());
        services.AddKeyedPants("secondary", PantsOpenOptions.InMemory());

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();
        IPantsDatabaseProvider primaryProvider =
            serviceProvider.GetRequiredKeyedService<IPantsDatabaseProvider>("primary");
        IPantsDatabaseProvider secondaryProvider =
            serviceProvider.GetRequiredKeyedService<IPantsDatabaseProvider>("secondary");
        IPantsDatabase primary = await primaryProvider.GetDatabaseAsync();
        IPantsDatabase secondary = await secondaryProvider.GetDatabaseAsync();

        Assert.NotSame(primaryProvider, secondaryProvider);
        Assert.NotSame(primary, secondary);
    }
}
