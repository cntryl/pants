namespace Pants;

readonly record struct CompactionResult(
    long BytesRewritten,
    bool PersistenceAnomaly);
