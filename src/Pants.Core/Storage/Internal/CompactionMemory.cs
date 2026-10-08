namespace Cntryl.Pants.Storage.Internal;

/// <summary>
///     The maintenance memory a compaction runs under: the pool its merge buffers reserve from,
///     the gate that gives each compaction round exclusive use of that pool, and an optional output
///     size cap from a publisher whose own memory envelope must also fit the pool.
/// </summary>
sealed record CompactionMemory(
    ResourceBudget Budget,
    MaintenanceMemoryGate Gate,
    long? OutputPartitionTargetBytes = null);
