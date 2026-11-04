using System.Diagnostics;

namespace Cntryl.Pants.Runtime.Internal.Services.Flush;

sealed class FlushRuntimeService(
    int capacity,
    ILocalFlushStore? flushStore,
    RuntimeTelemetry telemetry,
    TimeProvider? timeProvider = null)
    : ChannelRuntimeService<FlushRuntimeRequest, FlushRuntimeResult>(capacity)
{
    public ValueTask FlushAsync(RuntimeState state, CancellationToken cancellationToken = default) =>
        FlushAsync(state.OrdinaryFlushOrigin, cancellationToken);

    public async ValueTask FlushAsync(
        MetadataOrigin origin,
        CancellationToken cancellationToken = default) =>
        _ = await ExecuteAsync(new FlushAllRuntimeRequest(origin), cancellationToken)
            .ConfigureAwait(false);

    public ValueTask FlushAsync(RuntimeState state, ColumnFamilyIdentity columnFamily,
        CancellationToken cancellationToken = default) =>
        FlushAsync(columnFamily, state.OrdinaryFlushOrigin, cancellationToken);

    public async ValueTask FlushAsync(
        ColumnFamilyIdentity columnFamily,
        MetadataOrigin origin,
        CancellationToken cancellationToken = default) =>
        _ = await ExecuteAsync(
                new FlushColumnFamilyRuntimeRequest(columnFamily, origin),
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
        var store = flushStore ??
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
        ILocalFlushStore store,
        FlushAllRuntimeRequest request)
    {
        store.Flush(request.Origin);
        return ValueTask.FromResult(default(FlushRuntimeResult));
    }

    static ValueTask<FlushRuntimeResult> FlushColumnFamily(
        ILocalFlushStore store,
        FlushColumnFamilyRuntimeRequest request)
    {
        store.Flush(request.ColumnFamily, request.Origin);
        return ValueTask.FromResult(default(FlushRuntimeResult));
    }

    FlushRuntimeResult PublishFrozenMemtable(
        ILocalFlushStore store,
        PublishFrozenMemtableRuntimeRequest request)
    {
        var publicationPlan = request.PublicationPlan;
        var clock = timeProvider ?? TimeProvider.System;
        long? publicationStarted = null;
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

            publicationStarted = clock.GetTimestamp();
            var result = store.PublishFrozenFlushPlan(request.Frozen, publicationPlan);
            return new FlushRuntimeResult(new FrozenFlushRuntimeResult(
                publicationPlan,
                result.PersistenceAnomaly, publicationStarted, clock.GetTimestamp(), request.Frozen.Id));
        }
        catch (Exception exception)
        {
            var result = new FrozenFlushRuntimeResult(
                publicationPlan,
                false, publicationStarted, clock.GetTimestamp(), request.Frozen.Id);
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
