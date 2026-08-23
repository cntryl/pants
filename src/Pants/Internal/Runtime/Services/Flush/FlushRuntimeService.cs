using System.Diagnostics;

namespace Pants;

sealed class FlushRuntimeService(
    int capacity,
    LocalDiskStore? diskStore,
    RuntimeTelemetry telemetry)
    : ChannelRuntimeService<FlushRuntimeRequest, FlushRuntimeResult>(capacity)
{
    public async ValueTask FlushAsync(
        PantsRuntimeState state,
        CancellationToken cancellationToken = default) =>
        _ = await ExecuteAsync(new FlushAllRuntimeRequest(state), cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask FlushAsync(
        PantsRuntimeState state,
        ColumnFamilyIdentity columnFamily,
        CancellationToken cancellationToken = default) =>
        _ = await ExecuteAsync(
                new FlushColumnFamilyRuntimeRequest(state, columnFamily),
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<Task<FrozenFlushRuntimeResult>> ScheduleFrozenFlushAsync(
        FrozenMemtableFlush frozen,
        FlushPublicationPlan? publicationPlan,
        CancellationToken cancellationToken = default)
    {
        var response = await ScheduleAsync(
                new PublishFrozenMemtableRuntimeRequest(frozen, publicationPlan),
                cancellationToken)
            .ConfigureAwait(false);
        return ReadFrozenResultAsync(response);
    }

    protected override ValueTask<FlushRuntimeResult> DispatchAsync(
        FlushRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = diskStore ??
            throw new PantsInternalException("A flush runtime request requires local storage.");
        return request switch
        {
            FlushAllRuntimeRequest flush => FlushAll(store, flush),
            FlushColumnFamilyRuntimeRequest flush => FlushColumnFamily(store, flush),
            PublishFrozenMemtableRuntimeRequest publish =>
                ValueTask.FromResult(PublishFrozenMemtable(store, publish)),
            _ => throw new PantsInternalException(
                $"Unknown flush runtime request '{request.GetType().Name}'.")
        };
    }

    static ValueTask<FlushRuntimeResult> FlushAll(
        LocalDiskStore store,
        FlushAllRuntimeRequest request)
    {
        store.Flush(request.State);
        return ValueTask.FromResult(default(FlushRuntimeResult));
    }

    static ValueTask<FlushRuntimeResult> FlushColumnFamily(
        LocalDiskStore store,
        FlushColumnFamilyRuntimeRequest request)
    {
        store.Flush(request.State, request.ColumnFamily);
        return ValueTask.FromResult(default(FlushRuntimeResult));
    }

    FlushRuntimeResult PublishFrozenMemtable(
        LocalDiskStore store,
        PublishFrozenMemtableRuntimeRequest request)
    {
        var publicationPlan = request.PublicationPlan;
        try
        {
            if (publicationPlan is null)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    publicationPlan = store.BuildFrozenFlushPlan(request.Frozen);
                }
                finally
                {
                    telemetry.RecordFlushBuild(Stopwatch.GetElapsedTime(started));
                }
            }

            var publicationStarted = Stopwatch.GetTimestamp();
            try
            {
                var result = store.PublishFrozenFlushPlan(request.Frozen, publicationPlan);
                return new FlushRuntimeResult(new FrozenFlushRuntimeResult(
                    publicationPlan,
                    result.PersistenceAnomaly));
            }
            finally
            {
                telemetry.RecordFlushPublication(
                    Stopwatch.GetElapsedTime(publicationStarted));
            }
        }
        catch (Exception exception)
        {
            var result = new FrozenFlushRuntimeResult(
                publicationPlan,
                PersistenceAnomaly: false);
            throw new FrozenFlushRuntimeException(result, exception);
        }
    }

    static async Task<FrozenFlushRuntimeResult> ReadFrozenResultAsync(
        Task<FlushRuntimeResult> response)
    {
        var result = await response.ConfigureAwait(false);
        return result.Frozen ??
            throw new PantsInternalException("A frozen flush returned no publication result.");
    }
}
