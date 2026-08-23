namespace Cntryl.Pants.Storage.Internal.Compaction;

readonly record struct CompactionResult(
    long BytesRewritten,
    int PublicationCount,
    bool PersistenceAnomaly);
