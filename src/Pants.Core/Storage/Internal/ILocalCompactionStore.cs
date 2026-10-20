namespace Cntryl.Pants.Storage.Internal;

interface ILocalCompactionStore
{
    ValueTask<CompactionResult> DrainCompactionDebtAsync(
        CompactionWorkSnapshot snapshot,
        CompactionWorkSnapshotProvider snapshotProvider,
        CompactionPublicationDispatcher publicationDispatcher,
        CloudCompactionOutputPublisher? outputPublisher,
        Action<long>? publicationCompleted = null,
        CompactionMemory? memory = null,
        Func<IReadOnlyList<string>, CancellationToken, ValueTask<IDisposable?>>? prepareInputs = null,
        CompactionOutputStaging? staging = null,
        CancellationToken cancellationToken = default);

    ValueTask<CompactionResult> CompactAsync(
        CompactionWorkSnapshot snapshot,
        CompactionWorkSnapshotProvider snapshotProvider,
        CompactionPublicationDispatcher publicationDispatcher,
        bool force,
        CloudCompactionOutputPublisher? outputPublisher,
        Action<long>? publicationCompleted = null,
        CompactionMemory? memory = null,
        Func<IReadOnlyList<string>, CancellationToken, ValueTask<IDisposable?>>? prepareInputs = null,
        CompactionOutputStaging? staging = null,
        CancellationToken cancellationToken = default);
}
