namespace Cntryl.Pants;

public sealed record PantsRuntimeConfiguration(
    PantsPerformanceGoal PerformanceGoal,
    PantsWorkloadProfile WorkloadProfile,
    TimeSpan StorageTimeout,
    TimeSpan? RuntimeResponseTimeout,
    TimeSpan ShutdownTimeout)
{
    /// <summary>
    ///     Age after which an active snapshot is reported as long-lived. The snapshot remains
    ///     registered until its transaction or scan is disposed.
    /// </summary>
    public TimeSpan LongLivedSnapshotWarningThreshold { get; init; } = TimeSpan.FromHours(1);

    public static PantsRuntimeConfiguration Default { get; } = new(
        PantsPerformanceGoal.Latency,
        PantsWorkloadProfile.Mixed,
        TimeSpan.FromSeconds(30),
        null,
        TimeSpan.FromSeconds(30));
}
