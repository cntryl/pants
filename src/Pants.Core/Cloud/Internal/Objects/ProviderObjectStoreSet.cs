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
        var opened = new List<ICloudObjectStore>(3);
        try
        {
            var wal = await OpenAsync(topology.Wal).ConfigureAwait(false);
            var sst = await OpenAsync(topology.Sst).ConfigureAwait(false);
            var control = await OpenAsync(topology.Control).ConfigureAwait(false);
            return new ProviderObjectStoreSet(wal, sst, control);
        }
        catch
        {
            await DisposeDistinctAsync(opened).ConfigureAwait(false);
            throw;
        }

        async ValueTask<ICloudObjectStore> OpenAsync(PantsCloudStorageLocation location)
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

    static async ValueTask DisposeDistinctAsync(IEnumerable<ICloudObjectStore> stores)
    {
        var distinctStores = new HashSet<ICloudObjectStore>(ReferenceEqualityComparer.Instance);
        foreach (var store in stores)
        {
            if (distinctStores.Add(store))
            {
                await store.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
