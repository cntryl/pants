namespace Cntryl.Pants.Observability;

/// <summary>The local-storage operation associated with a rejected admission.</summary>
public enum PantsStorageAdmissionKind
{
    Wal,
    TransactionSpill,
    Flush,
    Compaction,
    FlushHeadroom,
    StartupResidue,
    RecoverySpool,
    Hydration
}
