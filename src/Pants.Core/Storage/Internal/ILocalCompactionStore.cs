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
        ResourceBudget? compactionBudget = null,
        Func<IReadOnlyList<string>, CancellationToken, ValueTask>? prepareInputs = null,
        CancellationToken cancellationToken = default);
}
