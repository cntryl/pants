namespace Cntryl.Pants.Observability;

/// <summary>Why durable WAL history cannot currently be retired.</summary>
public enum PantsWalRetirementBlockedCause
{
    None,
    FamilyFlushFrontier,
    RetainedWalByteLimit,
    RetainedWalSegmentLimit,
    MaintenanceBudget,
    InvalidWalSegment
}
