namespace Cntryl.Pants.Runtime.Internal.Services.Compaction;

sealed record CompactionRuntimeRequest(
    RuntimeState State,
    bool Force,
    CloudCompactionOutputPublisher? OutputPublisher,
    bool FlushMutableOperations);
