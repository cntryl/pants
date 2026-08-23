namespace Cntryl.Pants.DependencyInjection;

internal sealed record PantsDatabaseRegistration(
    Func<IServiceProvider, PantsOpenOptions> OptionsFactory);
