namespace Cntryl.Pants;

sealed record CompactionRuntimeRequest(
    PantsRuntimeState State,
    bool Force,
    CloudCompactionOutputPublisher? OutputPublisher,
    bool FlushMutableOperations);
