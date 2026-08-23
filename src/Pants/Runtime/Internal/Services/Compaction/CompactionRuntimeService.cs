namespace Cntryl.Pants;

sealed class CompactionRuntimeService(
    int capacity,
    LocalDiskStore? diskStore,
    RuntimeTelemetry telemetry)
    : ChannelRuntimeService<CompactionRuntimeRequest, CompactionResult>(capacity)
{
    int _compactingSsts;

    public int CompactingSsts => Volatile.Read(ref _compactingSsts);

    public ValueTask<CompactionResult> CompactAsync(
        PantsRuntimeState state,
        bool force,
        CloudCompactionOutputPublisher? outputPublisher,
        bool flushMutableOperations = true,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            new CompactionRuntimeRequest(
                state,
                force,
                outputPublisher,
                flushMutableOperations),
            cancellationToken);

    protected override async ValueTask<CompactionResult> DispatchAsync(
        CompactionRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        var store = diskStore ??
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
