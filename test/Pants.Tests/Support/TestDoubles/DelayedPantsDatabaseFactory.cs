using Cntryl.Pants.DependencyInjection;

namespace Cntryl.Pants.Tests;

internal sealed class DelayedPantsDatabaseFactory : IPantsDatabaseFactory
{
    private readonly TaskCompletionSource _allowOpen =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _openCount;

    public int OpenCount => Volatile.Read(ref _openCount);

    public async ValueTask<IPantsDatabase> OpenAsync(
        PantsOpenOptions options,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _openCount);
        await _allowOpen.Task.WaitAsync(cancellationToken);
        return await PantsDatabase.OpenAsync(options, cancellationToken);
    }

    public void AllowOpen() => _allowOpen.TrySetResult();
}
