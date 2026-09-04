namespace Cntryl.Pants.Runtime.Internal.Services.Compaction;

sealed class CompactionRuntimeService(
    int capacity,
    ILocalCompactionStore? compactionStore,
    RuntimeTelemetry telemetry,
    long memoryBudgetBytes)
    : ChannelRuntimeService<CompactionRuntimeRequest, CompactionResult>(capacity)
{
    int _compactingSsts;

    public ResourceBudget BufferBudget { get; } = new(memoryBudgetBytes);

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
            return await store.CompactAsync(
                    request.State,
                    request.Force,
                    request.OutputPublisher,
                    request.FlushMutableOperations,
                    telemetry.RecordCompaction,
                    BufferBudget,
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
