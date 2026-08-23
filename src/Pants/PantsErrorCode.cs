namespace Cntryl.Pants;

public enum PantsErrorCode
{
    Io,
    NotFound,
    InvalidArgument,
    Corruption,
    NotSupported,
    Internal,
    InvalidPath,
    NoSpace,
    RecoveryFailed,
    CompatibilityError,
    WriteStall,
    MemoryModeViolation,
    Fenced,
    LeaseHeld,
    LeaseUnavailable,
    LeaseIndeterminate,
    LeaseEpochExhausted,
    WriteConflict,
    Aborted,
    Busy,
    Timeout,
    ResourceLimit
}
