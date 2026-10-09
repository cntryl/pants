namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

/// <summary>
///     The resources one cloud WAL retirement turn may use: the shared maintenance memory pool and
///     its gate, the ranged-read page size, and the work quantum after which a turn yields at its
///     next acknowledged frame boundary.
/// </summary>
sealed record WalRetirementSettings(
    ResourceBudget Budget,
    MaintenanceMemoryGate Gate,
    int PageBytes,
    TimeSpan Quantum,
    TimeProvider TimeProvider)
{
    public const int DefaultPageBytes = 256 * 1024;

    const long StandaloneBudgetBytes = 64L * 1024 * 1024;

    public static TimeSpan DefaultQuantum { get; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Settings with a private pool, for a persistence that shares none.</summary>
    public static WalRetirementSettings Standalone() => new(
        new ResourceBudget(StandaloneBudgetBytes),
        new MaintenanceMemoryGate(),
        DefaultPageBytes,
        DefaultQuantum,
        TimeProvider.System);
}
