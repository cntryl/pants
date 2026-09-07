namespace Cntryl.Pants.Runtime.Internal.Flush;

static class MemtableWritePressure
{
    public const int MaximumImmutableMemtablesPerColumnFamily = 10;

    public static long GetTotalBytes(RuntimeState state) => checked(
        state.ActiveMemtableBytes.Values.Sum() +
        state.ImmutableMemtableFlushes.Values.Sum(static flush => flush.Frozen.SizeBytes));

    public static bool IsStalled(RuntimePlan options, RuntimeState state) =>
        IsStalled(options, state, state.ActiveMemtableBytes.Keys);

    public static bool IsStalled(
        RuntimePlan options,
        RuntimeState state,
        IEnumerable<ColumnFamilyIdentity> identities) =>
        options.Storage is not PantsStorageConfiguration.InMemory &&
        (GetTotalBytes(state) >= GetHardLimitBytes(options) ||
         identities.Any(identity =>
             IsQueueFull(state, identity) || HasCriticalL0Debt(options, state, identity)));

    /// <summary>
    ///     The ceiling on level-0 slots a single column family may hold: the files compaction is
    ///     already entitled to merge, plus the immutable memtables queued to become files, plus the
    ///     one generation currently being filled.
    /// </summary>
    public static int GetL0HardCeiling(RuntimePlan options) =>
        options.Compaction.L0FileCountTrigger +
        MaximumImmutableMemtablesPerColumnFamily +
        1;

    /// <summary>
    ///     Whether a family holds every level-0 slot it is entitled to.
    /// </summary>
    /// <remarks>
    ///     An active memtable that has already started filling holds its slot, so it is counted but
    ///     must not be what pushes the family over: refusing the write that would finish the very
    ///     generation whose flush releases the slot would wedge the family permanently. The write
    ///     that fills it is admitted and the one after it stalls.
    /// </remarks>
    public static bool HasCriticalL0Debt(
        RuntimePlan options,
        RuntimeState state,
        ColumnFamilyIdentity identity)
    {
        var published = state.PublishedL0FileCounts.GetValueOrDefault(identity.Id);
        var queued = state.ImmutableMemtableFlushes.Values.Count(flush =>
            flush.Frozen.ColumnFamily == identity);
        return published + queued >= GetL0HardCeiling(options);
    }

    public static bool IsQueueFull(
        RuntimeState state,
        ColumnFamilyIdentity identity) =>
        state.ImmutableMemtableFlushes.Values.Count(flush =>
            flush.Frozen.ColumnFamily == identity) >= MaximumImmutableMemtablesPerColumnFamily;

    static long GetHardLimitBytes(RuntimePlan options) =>
        options.MemtableFlushThresholdBytes > long.MaxValue / 2
            ? long.MaxValue
            : options.MemtableFlushThresholdBytes * 2;
}
