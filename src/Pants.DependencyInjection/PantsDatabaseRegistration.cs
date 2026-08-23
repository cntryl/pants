namespace Cntryl.Pants.DependencyInjection;

sealed record PantsDatabaseRegistration(
    Func<IServiceProvider, PantsOpenOptions> OptionsFactory);
