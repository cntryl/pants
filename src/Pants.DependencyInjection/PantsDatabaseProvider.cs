namespace Cntryl.Pants.DependencyInjection;

sealed class PantsDatabaseProvider : IPantsDatabaseProvider, IAsyncDisposable
{
    readonly IPantsDatabaseFactory _databaseFactory;
    readonly object _gate = new();
    readonly CancellationTokenSource _lifetimeCancellation = new();
    readonly Func<IServiceProvider, PantsOpenOptions> _optionsFactory;
    readonly IServiceProvider _serviceProvider;
    Task<IPantsDatabase>? _databaseTask;
    bool _disposed;

    public PantsDatabaseProvider(
        IServiceProvider serviceProvider,
        IPantsDatabaseFactory databaseFactory,
        PantsDatabaseRegistration registration)
    {
        _serviceProvider = serviceProvider;
        _databaseFactory = databaseFactory;
        _optionsFactory = registration.OptionsFactory;
    }

    public async ValueTask DisposeAsync()
    {
        Task<IPantsDatabase>? databaseTask;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetimeCancellation.Cancel();
            databaseTask = _databaseTask;
        }

        try
        {
            if (databaseTask is not null)
            {
                var database = await databaseTask.ConfigureAwait(false);
                await database.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Disposal canceled initialization before a database was published.
        }
        finally
        {
            _lifetimeCancellation.Dispose();
        }
    }

    public ValueTask<IPantsDatabase> GetDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        Task<IPantsDatabase> databaseTask;
        TaskCompletionSource<IPantsDatabase>? initialization = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_databaseTask is null)
            {
                initialization = new TaskCompletionSource<IPantsDatabase>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _databaseTask = initialization.Task;
            }

            databaseTask = _databaseTask;
        }

        if (initialization is not null)
        {
            _ = InitializeDatabaseAsync(initialization, _lifetimeCancellation.Token);
        }

        return cancellationToken.CanBeCanceled
            ? new ValueTask<IPantsDatabase>(databaseTask.WaitAsync(cancellationToken))
            : new ValueTask<IPantsDatabase>(databaseTask);
    }

    async Task InitializeDatabaseAsync(
        TaskCompletionSource<IPantsDatabase> initialization,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = _optionsFactory(_serviceProvider);
            if (options is null)
            {
                throw new InvalidOperationException(
                    "The registered Pants options factory returned null.");
            }

            var database = await _databaseFactory
                .OpenAsync(options, cancellationToken)
                .ConfigureAwait(false);
            initialization.TrySetResult(database);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            initialization.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            initialization.TrySetException(exception);
        }
    }
}
