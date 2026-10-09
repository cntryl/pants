namespace Cntryl.Pants.Runtime.Internal.Services.Compaction;

sealed class CompactionRuntimeService(
    int capacity,
    ILocalCompactionStore? compactionStore,
    RuntimeTelemetry telemetry,
    CompactionMemory memory)
    : ChannelRuntimeService<CompactionRuntimeRequest, CompactionResult>(capacity)
{
    int _compactingSsts;

    public ResourceBudget BufferBudget => memory.Budget;

    public int CompactingSsts => Volatile.Read(ref _compactingSsts);

    public ValueTask<CompactionResult> CompactAsync(
        RuntimeState state,
        bool force,
        CloudCompactionOutputPublisher? outputPublisher,
        bool flushMutableOperations = true,
        Func<IReadOnlyList<string>, CancellationToken, ValueTask>? prepareInputs = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            new CompactionRuntimeRequest(
                state,
                force,
                outputPublisher,
                flushMutableOperations,
                prepareInputs),
            cancellationToken);

    public ValueTask<CompactionResult> DrainDebtAsync(
        RuntimeState state,
        CloudCompactionOutputPublisher? outputPublisher,
        Func<IReadOnlyList<string>, CancellationToken, ValueTask>? prepareInputs = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            new CompactionRuntimeRequest(
                state,
                true,
                outputPublisher,
                true,
                prepareInputs,
                DrainDebt: true),
            cancellationToken);

    protected override async ValueTask<CompactionResult> DispatchAsync(
        CompactionRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        var store = compactionStore ??
                    throw new PantsInternalException("A compaction runtime request requires local storage.");
        try
        {
            Volatile.Write(
                ref _compactingSsts,
                store.CountCompactionInputs(request.State, request.Force));
            if (request.DrainDebt)
            {
                return await store.DrainCompactionDebtAsync(
                        request.State,
                        request.OutputPublisher,
                        telemetry.RecordCompaction,
                        memory,
                        request.PrepareInputs,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await store.CompactAsync(
                    request.State,
                    request.Force,
                    request.OutputPublisher,
                    request.FlushMutableOperations,
                    telemetry.RecordCompaction,
                    memory,
                    request.PrepareInputs,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            telemetry.RecordCompactionFailure();
            throw;
        }
        finally
        {
            Volatile.Write(ref _compactingSsts, 0);
        }
    }
}
