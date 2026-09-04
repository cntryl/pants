namespace Cntryl.Pants;

sealed record PantsDatabaseRegistration(
    Func<IServiceProvider, PantsOpenOptions> OptionsFactory);
