namespace Cntryl.Pants;

public sealed record PantsMemoryConfiguration(
    PantsMemoryBudget Budget,
    PantsBlockCachePolicy BlockCachePolicy,
    long? MemtableSizeLimitBytes = null,
    long? MemtableFlushThresholdBytes = null,
    long? TransactionMemoryPoolBytes = null,
    int? WalBufferSizeBytes = null)
{
    public static PantsMemoryConfiguration Default { get; } = new(
        PantsMemoryBudget.Auto,
        PantsBlockCachePolicy.Lru);
}
