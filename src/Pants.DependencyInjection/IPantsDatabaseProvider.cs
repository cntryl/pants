namespace Cntryl.Pants;

public interface IPantsDatabaseProvider
{
    ValueTask<IPantsDatabase> GetDatabaseAsync(
        CancellationToken cancellationToken = default);
}
