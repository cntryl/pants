using Cntryl.Pants.Support.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Pants;

public sealed class PantsDependencyInjectionTests
{
    [Fact]
    public async Task AddPantsRegistersOneLazyAsyncDatabaseProvider()
    {
        var options = PantsOpenOptions.InMemory();
        var services = new ServiceCollection();

        services.AddPants(options);

        await using var serviceProvider = services.BuildServiceProvider();
        var provider = serviceProvider.GetRequiredService<IPantsDatabaseProvider>();
        var first = await provider.GetDatabaseAsync();
        var second = await provider.GetDatabaseAsync();

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

        await using var serviceProvider = services.BuildServiceProvider();
        var provider = serviceProvider.GetRequiredService<IPantsDatabaseProvider>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetDatabaseAsync(cancellation.Token).AsTask());

        databaseFactory.AllowOpen();
        var database = await provider.GetDatabaseAsync();

        Assert.NotNull(database);
        Assert.Equal(1, databaseFactory.OpenCount);
    }

    [Fact]
    public async Task ContainerOwnsAndAsynchronouslyDisposesOpenedDatabase()
    {
        var services = new ServiceCollection();
        services.AddPants(PantsOpenOptions.InMemory());
        var serviceProvider = services.BuildServiceProvider();
        var provider = serviceProvider.GetRequiredService<IPantsDatabaseProvider>();
        var database = await provider.GetDatabaseAsync();

        await serviceProvider.DisposeAsync();

        await Assert.ThrowsAsync<PantsAbortedException>(() => database.Diagnostics.GetRuntimeMetricsAsync().AsTask());
    }

    [Fact]
    public async Task AddKeyedPantsSupportsIndependentNamedDatabases()
    {
        var services = new ServiceCollection();
        services.AddKeyedPants("primary", PantsOpenOptions.InMemory());
        services.AddKeyedPants("secondary", PantsOpenOptions.InMemory());

        await using var serviceProvider = services.BuildServiceProvider();
        var primaryProvider =
            serviceProvider.GetRequiredKeyedService<IPantsDatabaseProvider>("primary");
        var secondaryProvider =
            serviceProvider.GetRequiredKeyedService<IPantsDatabaseProvider>("secondary");
        var primary = await primaryProvider.GetDatabaseAsync();
        var secondary = await secondaryProvider.GetDatabaseAsync();

        Assert.NotSame(primaryProvider, secondaryProvider);
        Assert.NotSame(primary, secondary);
    }
}
