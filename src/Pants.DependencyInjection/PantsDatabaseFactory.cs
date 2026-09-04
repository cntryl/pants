namespace Cntryl.Pants;

sealed class PantsDatabaseFactory : IPantsDatabaseFactory
{
    public ValueTask<IPantsDatabase> OpenAsync(
        PantsOpenOptions options,
        CancellationToken cancellationToken = default) =>
        PantsDatabase.OpenAsync(options, cancellationToken);
}
