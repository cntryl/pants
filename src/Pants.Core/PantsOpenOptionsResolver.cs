namespace Cntryl.Pants;

/// <summary>Resolves open options into the effective values the runtime will use.</summary>
public static class PantsOpenOptionsResolver
{
    public static PantsResolvedConfiguration Resolve(PantsOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var plan = RuntimePlan.Resolve(options);
        return new PantsResolvedConfiguration(
            plan.MemoryBudgetBytes,
            plan.TransactionMemoryPoolBytes,
            plan.CompactionMemoryPoolBytes,
            plan.ScanMemoryPoolBytes,
            plan.MemtableSizeLimitBytes,
            plan.MemtableFlushThresholdBytes,
            plan.BlockCacheBytes,
            plan.BlockSizeBytes,
            plan.TargetSstSizeBytes,
            plan.WalBufferSizeBytes,
            plan.L0CompactionTrigger,
            plan.StorageTimeout,
            plan.RuntimeResponseTimeout,
            options.Runtime.ShutdownTimeout);
    }
}
