using Cntryl.Pants;
using Cntryl.Pants.DependencyInjection;
using Cntryl.Pants.DependencyInjection.Options;
using Cntryl.Pants.DependencyInjection.Options.Internal;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static class PantsServiceCollectionExtensions
{
    public static OptionsBuilder<PantsDatabaseOptions> AddPants(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = AddValidatedOptions(
            services,
            Microsoft.Extensions.Options.Options.DefaultName);
        services.AddPants(serviceProvider => PantsDatabaseOptionsMapper.Create(
            serviceProvider.GetRequiredService<IOptions<PantsDatabaseOptions>>().Value));
        return optionsBuilder;
    }

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

    public static OptionsBuilder<PantsDatabaseOptions> AddKeyedPants(
        this IServiceCollection services,
        string serviceKey)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceKey);

        var optionsBuilder = AddValidatedOptions(services, serviceKey);
        services.AddKeyedPants(
            (object)serviceKey,
            serviceProvider => PantsDatabaseOptionsMapper.Create(
                serviceProvider
                    .GetRequiredService<IOptionsMonitor<PantsDatabaseOptions>>()
                    .Get(serviceKey)));
        return optionsBuilder;
    }

    static OptionsBuilder<PantsDatabaseOptions> AddValidatedOptions(
        IServiceCollection services,
        string name)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<PantsDatabaseOptions>,
                PantsDatabaseOptionsValidator>());
        return services.AddOptions<PantsDatabaseOptions>(name).ValidateOnStart();
    }
}
