namespace Cntryl.Pants.Runtime.Internal.Flush;

static class MemtableWritePressure
{
    public const int MaximumImmutableMemtablesPerColumnFamily = 10;

    public static long GetTotalBytes(RuntimeState state) => checked(
        state.ActiveMemtableBytes.Values.Sum() +
        state.ImmutableMemtableFlushes.Values.Sum(static flush => flush.Frozen.SizeBytes));

    public static bool IsStalled(RuntimePlan options, RuntimeState state) =>
        options.Storage is not PantsStorageConfiguration.InMemory &&
        (GetTotalBytes(state) >= GetHardLimitBytes(options) ||
         state.ActiveMemtableBytes.Keys.Any(identity => IsQueueFull(state, identity)));

    public static bool IsStalled(
        RuntimePlan options,
        RuntimeState state,
        IEnumerable<ColumnFamilyIdentity> identities) =>
        options.Storage is not PantsStorageConfiguration.InMemory &&
        (GetTotalBytes(state) >= GetHardLimitBytes(options) ||
         identities.Any(identity => IsQueueFull(state, identity)));

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
