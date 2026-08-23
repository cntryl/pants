namespace Cntryl.Pants;

readonly record struct CompactionResult(
    long BytesRewritten,
    int PublicationCount,
    bool PersistenceAnomaly);
