namespace Cntryl.Pants.Runtime.Internal;

/// <summary>
///     Splits one memory budget into transaction, compaction, memtable and shared SST read
///     allowances. Read resources divide their allowance between content and shared metadata.
/// </summary>
sealed record MemoryPools(
    long TransactionMemoryPoolBytes,
    long CompactionMemoryPoolBytes,
    long ScanMemoryPoolBytes,
    long MemtableSizeLimitBytes,
    long MemtableFlushThresholdBytes,
    long BlockCacheBytes)
{
    const long Mebibyte = 1024L * 1024;
    const long MaximumCompactionPoolBytes = 256 * Mebibyte;
    const long MaximumEconomyBlockCacheBytes = 256 * Mebibyte;

    public static MemoryPools Derive(
        long budgetBytes,
        PantsMemoryConfiguration memory,
        PantsStorageConfiguration storage,
        PantsPerformanceGoal performanceGoal,
        PantsWorkloadProfile workloadProfile)
    {
        var persistent = storage is not PantsStorageConfiguration.InMemory;
        var automaticMemtables = memory.MemtableSizeLimitBytes is null;
        var transactionPool = memory.TransactionMemoryPoolBytes ?? Math.Max(1, budgetBytes / 10);
        if (transactionPool > budgetBytes)
        {
            throw PantsException.ResourceLimit("Transaction memory pool exceeds the total memory budget.");
        }

        var remainingAfterRequiredPools = Math.Max(0, budgetBytes - transactionPool - 2);
        var desiredCompactionPool = Math.Min(budgetBytes / 5, MaximumCompactionPoolBytes);
        var compactionPool = Math.Min(desiredCompactionPool, remainingAfterRequiredPools);
        if (compactionPool <= 0)
        {
            throw PantsException.ResourceLimit("The memory budget leaves no capacity for bounded compaction.");
        }

        const long scanPool = 0;

        // Automatic persistent memtables leave a quarter of what remains for SST reads, so a
        // small budget still has block-cache capacity. Explicit memtables keep their full share.
        var readAndMemtableBytes = budgetBytes - transactionPool - compactionPool - scanPool;
        var automaticReadShare = automaticMemtables && persistent ? readAndMemtableBytes / 4 : 0;
        var maximumMemtable = (readAndMemtableBytes - automaticReadShare) / 2;
        if (maximumMemtable <= 0)
        {
            throw PantsException.ResourceLimit("The memory budget leaves no capacity for memtables.");
        }

        var memtableSizeLimit = memory.MemtableSizeLimitBytes ??
                                Math.Min(DesiredMemtable(performanceGoal, workloadProfile), maximumMemtable);
        var memtableFlushThreshold = memory.MemtableFlushThresholdBytes ?? memtableSizeLimit;
        Validate(budgetBytes, transactionPool, compactionPool, scanPool, memtableSizeLimit, memtableFlushThreshold);

        var blockCache = budgetBytes - transactionPool - compactionPool - scanPool - 2 * memtableSizeLimit;
        if (performanceGoal == PantsPerformanceGoal.Economy)
        {
            blockCache = Math.Min(blockCache, MaximumEconomyBlockCacheBytes);
        }

        if (blockCache <= 0 && persistent)
        {
            throw PantsException.ResourceLimit(
                "The memory budget leaves no block cache for SST reads after the memtables and " +
                "worker pools.");
        }

        return new MemoryPools(
            transactionPool,
            compactionPool,
            scanPool,
            memtableSizeLimit,
            memtableFlushThreshold,
            blockCache);
    }

    static long DesiredMemtable(PantsPerformanceGoal performanceGoal, PantsWorkloadProfile workloadProfile)
    {
        var baseMemtable = performanceGoal switch
        {
            PantsPerformanceGoal.Latency => 64 * Mebibyte,
            PantsPerformanceGoal.Throughput => 256 * Mebibyte,
            PantsPerformanceGoal.Economy => 32 * Mebibyte,
            _ => throw PantsException.InvalidArgument("Unknown performance goal.")
        };
        return workloadProfile switch
        {
            PantsWorkloadProfile.WriteHeavy => baseMemtable * 2,
            PantsWorkloadProfile.ReadMostly => baseMemtable / 2,
            _ => baseMemtable
        };
    }

    static void Validate(
        long budgetBytes,
        long transactionPool,
        long compactionPool,
        long scanPool,
        long memtableSizeLimit,
        long memtableFlushThreshold)
    {
        if (memtableSizeLimit <= 0 || memtableFlushThreshold <= 0)
        {
            throw PantsException.InvalidArgument("Memtable limits must be greater than zero.");
        }

        if (memtableFlushThreshold > memtableSizeLimit)
        {
            throw PantsException.InvalidArgument("Memtable flush threshold exceeds its size limit.");
        }

        long reservedBytes;
        try
        {
            reservedBytes = checked(2 * memtableSizeLimit + transactionPool + compactionPool + scanPool);
        }
        catch (OverflowException)
        {
            throw PantsException.ResourceLimit(
                "Configured memory pools overflow the total memory budget calculation.");
        }

        if (reservedBytes > budgetBytes)
        {
            throw PantsException.ResourceLimit(
                "Two memtables plus the transaction, compaction, and scan pools exceed the " +
                "total memory budget.");
        }
    }
}
