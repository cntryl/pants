namespace Cntryl.Pants;

public static class PantsErrorCodeExtensions
{
    /// <summary>Classifies an error code for retry, backpressure, fencing or halt.</summary>
    public static PantsErrorSeverity GetSeverity(this PantsErrorCode code) => code switch
    {
        PantsErrorCode.NotFound or
            PantsErrorCode.InvalidArgument or
            PantsErrorCode.NotSupported or
            PantsErrorCode.InvalidPath or
            PantsErrorCode.MemoryModeViolation or
            PantsErrorCode.WriteConflict => PantsErrorSeverity.Caller,

        PantsErrorCode.Io or
            PantsErrorCode.LeaseHeld or
            PantsErrorCode.LeaseUnavailable or
            PantsErrorCode.Aborted => PantsErrorSeverity.Transient,

        PantsErrorCode.NoSpace or
            PantsErrorCode.WriteStall or
            PantsErrorCode.Busy or
            PantsErrorCode.Timeout => PantsErrorSeverity.Backpressure,

        PantsErrorCode.Fenced or
            PantsErrorCode.LeaseEpochExhausted or
            PantsErrorCode.LeaseIndeterminate => PantsErrorSeverity.Fenced,

        PantsErrorCode.Internal or
            PantsErrorCode.ResourceLimit => PantsErrorSeverity.Defect,

        PantsErrorCode.Corruption or
            PantsErrorCode.RecoveryFailed or
            PantsErrorCode.CompatibilityError => PantsErrorSeverity.Fatal,

        _ => PantsErrorSeverity.Defect
    };
}
