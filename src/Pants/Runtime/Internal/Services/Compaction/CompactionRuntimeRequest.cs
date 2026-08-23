namespace Cntryl.Pants.Runtime.Internal.Services.Compaction;

sealed record CompactionRuntimeRequest(
    PantsRuntimeState State,
    bool Force,
    CloudCompactionOutputPublisher? OutputPublisher,
    bool FlushMutableOperations);
