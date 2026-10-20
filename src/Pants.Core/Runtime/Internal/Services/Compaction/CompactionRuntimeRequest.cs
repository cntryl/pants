namespace Cntryl.Pants.Runtime.Internal.Services.Compaction;

sealed record CompactionRuntimeRequest(
    CompactionWorkSnapshot Snapshot,
    CompactionWorkSnapshotProvider SnapshotProvider,
    CompactionPublicationDispatcher PublicationDispatcher,
    bool Force,
    CloudCompactionOutputPublisher? OutputPublisher,
    Func<IReadOnlyList<string>, CancellationToken, ValueTask<IDisposable?>>? PrepareInputs,
    CompactionOutputStaging? Staging,
    bool DrainDebt,
    long Generation,
    CancellationToken RequestCancellationToken);
