namespace Cntryl.Pants.Storage.Internal.Hybrid;

/// <summary>
///     What a local-disk reservation is for. Determines how much of the budget the operation may
///     consume: work that releases space is allowed headroom that user writes are not.
/// </summary>
enum StorageAdmissionKind
{
    Wal,
    TransactionSpill,
    RecoverySpool,
    Hydration,
    Flush,
    Compaction
}
