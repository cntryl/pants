using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Cntryl.Pants.Cloud.Internal.Objects;

sealed class ProviderObjectStoreSet(
    ICloudObjectStore wal,
    ICloudObjectStore sst,
    ICloudObjectStore control) : IAsyncDisposable
{
    int _disposed;

    public ICloudObjectStore Wal { get; } = wal;

    public ICloudObjectStore Sst { get; } = sst;

    public ICloudObjectStore Control { get; } = control;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await DisposeDistinctAsync([Wal, Sst, Control]).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public static async ValueTask<ProviderObjectStoreSet> OpenAsync(
        PantsCloudStorageTopology topology,
        TimeSpan timeout,
        HttpClient? httpClient,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topology);
        var opened = new ConcurrentBag<ICloudObjectStore>();
        try
        {
            var walTask = OpenAsync(topology.Wal);
            var sstTask = OpenAsync(topology.Sst);
            var controlTask = OpenAsync(topology.Control);
            await Task.WhenAll(walTask, sstTask, controlTask).ConfigureAwait(false);
            return new ProviderObjectStoreSet(
                await walTask.ConfigureAwait(false),
                await sstTask.ConfigureAwait(false),
                await controlTask.ConfigureAwait(false));
        }
        catch
        {
            try
            {
                await DisposeDistinctAsync(opened).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cleanup must not replace the original initialization failure or cancellation.
            }

            throw;
        }

        async Task<ICloudObjectStore> OpenAsync(PantsCloudStorageLocation location)
        {
            var store = await CloudObjectStoreFactory.CreateAsync(
                    location,
                    timeout,
                    httpClient,
                    timeProvider,
                    cancellationToken)
                .ConfigureAwait(false);
            opened.Add(store);
            return store;
        }
    }

    internal static async ValueTask DisposeDistinctAsync(IEnumerable<ICloudObjectStore> stores)
    {
        var distinctStores = new HashSet<ICloudObjectStore>(ReferenceEqualityComparer.Instance);
        List<Exception>? failures = null;
        foreach (var store in stores)
        {
            if (distinctStores.Add(store))
            {
                try
                {
                    await store.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }
        }

        if (failures is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures is { Count: > 1 })
        {
            throw new AggregateException(failures);
        }
    }
}
