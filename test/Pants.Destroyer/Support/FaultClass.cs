namespace Cntryl.Pants.Destroyer.Support;

/// <summary>
/// The class of fault a scenario injects. Ported verbatim (same 19 variants,
/// same expected-behavior mapping) from midge-destroyer's <c>scenario.rs</c>
/// <c>FaultClass</c>, so results are comparable across the two engines even
/// though this harness is xUnit-native rather than a standalone CLI.
/// </summary>
public enum FaultClass
{
    ProcessKill,
    ForcedReopen,
    StaleCacheCleanup,
    DroppedWrite,
    WalTruncationRace,
    ManifestInterruption,
    SstCorruption,
    CompactionRace,
    LeaseStalenessWindow,
    ProviderLatencySpike,
    RegionPartition,
    StrictAsyncDurabilityFlip,
    ExactWalPathFault,
    ManifestCheckpointCut,
    FlushCompactionBarrierFault,
    LeaseRenewalCut,
    MigrationBoundaryFault,
    AckBeforeReportCrash,
    CloudCacheLoss,
}

public enum FaultExpectation
{
    SafetyPreserved,
    TemporarilyUnavailable,
}

public static class FaultClassExtensions
{
    public static FaultExpectation ExpectedBehavior(this FaultClass fault) => fault switch
    {
        FaultClass.ProcessKill
            or FaultClass.ForcedReopen
            or FaultClass.StaleCacheCleanup
            or FaultClass.DroppedWrite
            or FaultClass.LeaseStalenessWindow
            or FaultClass.RegionPartition
            or FaultClass.WalTruncationRace
            or FaultClass.ManifestInterruption
            or FaultClass.CompactionRace
            or FaultClass.StrictAsyncDurabilityFlip
            or FaultClass.FlushCompactionBarrierFault
            or FaultClass.LeaseRenewalCut
            or FaultClass.MigrationBoundaryFault => FaultExpectation.TemporarilyUnavailable,
        FaultClass.SstCorruption
            or FaultClass.ExactWalPathFault
            or FaultClass.ManifestCheckpointCut
            or FaultClass.ProviderLatencySpike
            or FaultClass.AckBeforeReportCrash
            or FaultClass.CloudCacheLoss => FaultExpectation.SafetyPreserved,
        _ => throw new ArgumentOutOfRangeException(nameof(fault), fault, message: null),
    };
}
