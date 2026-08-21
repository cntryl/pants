using Microsoft.Extensions.DependencyInjection.Extensions;
using Pants;
using Pants.DependencyInjection;

namespace Microsoft.Extensions.DependencyInjection;

public static class PantsServiceCollectionExtensions
{
    public static IServiceCollection AddPants(
        this IServiceCollection services,
        PantsOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return services.AddPants(_ => options);
    }

    public static IServiceCollection AddPants(
        this IServiceCollection services,
        Func<IServiceProvider, PantsOpenOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);

        services.AddSingleton(new PantsDatabaseRegistration(optionsFactory));
        services.TryAddSingleton<IPantsDatabaseFactory, PantsDatabaseFactory>();
        services.TryAddSingleton<IPantsDatabaseProvider, PantsDatabaseProvider>();
        return services;
    }

    public static IServiceCollection AddKeyedPants(
        this IServiceCollection services,
        object serviceKey,
        PantsOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return services.AddKeyedPants(serviceKey, _ => options);
    }

    public static IServiceCollection AddKeyedPants(
        this IServiceCollection services,
        object serviceKey,
        Func<IServiceProvider, PantsOpenOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        ArgumentNullException.ThrowIfNull(optionsFactory);

        services.TryAddSingleton<IPantsDatabaseFactory, PantsDatabaseFactory>();
        services.AddKeyedSingleton<IPantsDatabaseProvider>(
            serviceKey,
            (serviceProvider, _) => new PantsDatabaseProvider(
                serviceProvider,
                serviceProvider.GetRequiredService<IPantsDatabaseFactory>(),
                new PantsDatabaseRegistration(optionsFactory)));
        return services;
    }
}
