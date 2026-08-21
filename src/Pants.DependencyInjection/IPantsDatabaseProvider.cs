namespace Pants.DependencyInjection;

public interface IPantsDatabaseProvider
{
    ValueTask<IPantsDatabase> GetDatabaseAsync(
        CancellationToken cancellationToken = default);
}
