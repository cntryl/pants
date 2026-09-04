namespace Cntryl.Pants;

public sealed record PantsRuntimeConfiguration(
    PantsPerformanceGoal PerformanceGoal,
    PantsWorkloadProfile WorkloadProfile,
    TimeSpan StorageTimeout,
    TimeSpan? RuntimeResponseTimeout,
    TimeSpan ShutdownTimeout)
{
    public static PantsRuntimeConfiguration Default { get; } = new(
        PantsPerformanceGoal.Latency,
        PantsWorkloadProfile.Mixed,
        TimeSpan.FromSeconds(30),
        null,
        TimeSpan.FromSeconds(30));
}
