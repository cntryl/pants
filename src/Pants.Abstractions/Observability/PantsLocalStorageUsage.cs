namespace Cntryl.Pants.Observability;

/// <summary>Per-class local-storage charges and outstanding reservation classes.</summary>
public sealed record PantsLocalStorageUsage
{
    public long WalBytes { get; init; }

    public long TransactionSpillBytes { get; init; }

    public long ResidentSstBytes { get; init; }

    public long StartupResidueBytes { get; init; }

    public long FlushStagingReservedBytes { get; init; }

    public long FlushHeadroomReservedBytes { get; init; }

    public long CompactionStagingReservedBytes { get; init; }

    /// <summary>Preallocated WAL headroom; zero when the runtime has no such reservation.</summary>
    public long WalHeadroomReservedBytes { get; init; }

    /// <summary>Exact WAL append reservations held while an append is in progress.</summary>
    public long WalAppendReservedBytes { get; init; }

    /// <summary>Scratch bytes reserved for WAL recovery spool files.</summary>
    public long RecoverySpoolReservedBytes { get; init; }

    /// <summary>Remote SST bytes reserved while hydrating the local cache.</summary>
    public long HydrationReservedBytes { get; init; }

    /// <summary>Outstanding flush and compaction reservations, excluding reusable flush headroom.</summary>
    public int Reservations { get; init; }
}
