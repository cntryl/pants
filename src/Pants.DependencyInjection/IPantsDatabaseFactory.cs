namespace Pants.DependencyInjection;

public interface IPantsDatabaseFactory
{
    ValueTask<IPantsDatabase> OpenAsync(
        PantsOpenOptions options,
        CancellationToken cancellationToken = default);
}
