namespace Cntryl.Pants.Observability;

/// <summary>Charged local working storage for a hybrid runtime.</summary>
public sealed record PantsHybridStorageBudgetSnapshot
{
    public long MaximumLocalBytes { get; init; }

    /// <summary>
    /// Physical local bytes plus all outstanding reservations. During a publication hand-off, the
    /// ledger can conservatively count both a file and its reservation until publication completes.
    /// </summary>
    public long TotalCommittedBytes { get; init; }

    public long FreeBytes { get; init; }

    public int UsagePercent { get; init; }

    public int PendingEvictions { get; init; }

    public PantsLocalStorageUsage Usage { get; init; } = new();

    public PantsStorageAdmissionBlock? BlockedAdmission { get; init; }

    public long AdmissionRejectionsTotal { get; init; }
}
