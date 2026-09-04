namespace Cntryl.Pants.Support.TestDoubles;

sealed class CapturingPantsDatabaseFactory : IPantsDatabaseFactory
{
    int _openCount;

    public int OpenCount => Volatile.Read(ref _openCount);

    public PantsOpenOptions? Options { get; private set; }

    public ValueTask<IPantsDatabase> OpenAsync(
        PantsOpenOptions options,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _openCount);
        Options = options;
        return PantsDatabase.OpenAsync(PantsOpenOptions.InMemory(), cancellationToken);
    }
}
