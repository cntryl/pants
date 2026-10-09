namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

/// <summary>
///     Sizes compaction output partitions from the maintenance pool that remains once the merge's
///     inputs hold their decoded blocks. One eighth of that remainder stays unassigned, so blocks
///     that grow as inputs advance and the one-entry handoff reservation still fit. A remainder too
///     small for a minimal partition fails before any output is written.
/// </summary>
static class CompactionPartitionSizing
{
    /// <summary>Smallest output partition a compaction may write.</summary>
    public const long MinimumOutputPartitionBytes = 4 * 1024;

    /// <summary>The share of what remains kept as slack is one over this.</summary>
    const long SlackDivisor = 8;

    /// <summary>
    ///     The output partition target given <paramref name="availableBytes" /> of the pool that is
    ///     not held by the inputs.
    /// </summary>
    /// <exception cref="PantsException">
    ///     The partition share of <paramref name="availableBytes" /> is below
    ///     <see cref="MinimumOutputPartitionBytes" />.
    /// </exception>
    public static long OutputPartitionTargetBytes(long requestedTargetBytes, long availableBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedTargetBytes);
        var remaining = Math.Max(0, availableBytes);
        var partitionBytes = remaining - (remaining / SlackDivisor);
        if (partitionBytes < MinimumOutputPartitionBytes)
        {
            throw PantsException.ResourceLimit(
                $"Compaction inputs leave {remaining} bytes of the maintenance pool for output; " +
                $"at least {MinimumOutputPartitionBytes} bytes of output partition are required.");
        }

        return Math.Min(requestedTargetBytes, partitionBytes);
    }
}
