namespace Cntryl.Pants.Storage.Internal;

interface ILocalCompactionStore
{
    int CountCompactionInputs(RuntimeState state, bool force);

    ValueTask<CompactionResult> CompactAsync(
        RuntimeState state,
        bool force,
        CloudCompactionOutputPublisher? outputPublisher,
        bool flushMutableOperations,
        Action<long>? publicationCompleted = null,
        CompactionMemory? memory = null,
        Func<IReadOnlyList<string>, CancellationToken, ValueTask>? prepareInputs = null,
        CompactionOutputStaging? staging = null,
        CancellationToken cancellationToken = default);
}
