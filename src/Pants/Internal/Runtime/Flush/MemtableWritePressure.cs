namespace Pants;

static class MemtableWritePressure
{
    public const int MaximumImmutableMemtablesPerColumnFamily = 10;

    public static long GetTotalBytes(PantsRuntimeState state) => checked(
        state.ActiveMemtableBytes.Values.Sum() +
        state.ImmutableMemtableFlushes.Values.Sum(static flush => flush.Frozen.SizeBytes));

    public static bool IsStalled(PantsOpenOptions options, PantsRuntimeState state) =>
        options.Storage is not PantsStorageConfiguration.InMemory &&
        (GetTotalBytes(state) >= GetHardLimitBytes(options) ||
         state.ActiveMemtableBytes.Keys.Any(identity => IsQueueFull(state, identity)));

    public static bool IsStalled(
        PantsOpenOptions options,
        PantsRuntimeState state,
        IEnumerable<ColumnFamilyIdentity> identities) =>
        options.Storage is not PantsStorageConfiguration.InMemory &&
        (GetTotalBytes(state) >= GetHardLimitBytes(options) ||
         identities.Any(identity => IsQueueFull(state, identity)));

    public static bool IsQueueFull(
        PantsRuntimeState state,
        ColumnFamilyIdentity identity) =>
        state.ImmutableMemtableFlushes.Values.Count(flush =>
            flush.Frozen.ColumnFamily == identity) >= MaximumImmutableMemtablesPerColumnFamily;

    static long GetHardLimitBytes(PantsOpenOptions options) =>
        options.MemtableFlushThresholdBytes > long.MaxValue / 2
            ? long.MaxValue
            : options.MemtableFlushThresholdBytes * 2;
}
