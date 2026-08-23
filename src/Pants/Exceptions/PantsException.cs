namespace Cntryl.Pants;

public abstract class PantsException : Exception
{
    protected PantsException(PantsErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    protected PantsException(PantsErrorCode code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public PantsErrorCode Code { get; }

    internal static PantsException Create(
        PantsErrorCode code,
        string message,
        Exception? innerException = null) => code switch
        {
            PantsErrorCode.Io => new PantsIOException(message, innerException),
            PantsErrorCode.NotFound => new PantsNotFoundException(message, innerException),
            PantsErrorCode.InvalidArgument => new PantsInvalidArgumentException(message, innerException),
            PantsErrorCode.Corruption => new PantsCorruptionException(message, innerException),
            PantsErrorCode.NotSupported => new PantsNotSupportedException(message, innerException),
            PantsErrorCode.Internal => new PantsInternalException(message, innerException),
            PantsErrorCode.InvalidPath => new PantsInvalidPathException(message, innerException),
            PantsErrorCode.NoSpace => new PantsNoSpaceException(message, innerException),
            PantsErrorCode.RecoveryFailed => new PantsRecoveryFailedException(message, innerException),
            PantsErrorCode.CompatibilityError => new PantsCompatibilityException(message, innerException),
            PantsErrorCode.WriteStall => new PantsWriteStallException(message, innerException),
            PantsErrorCode.MemoryModeViolation => new PantsMemoryModeViolationException(message, innerException),
            PantsErrorCode.Fenced => new PantsFencedException(message, innerException),
            PantsErrorCode.LeaseHeld => new PantsLeaseHeldException(message, innerException),
            PantsErrorCode.LeaseUnavailable => new PantsLeaseUnavailableException(message, innerException),
            PantsErrorCode.LeaseIndeterminate => new PantsLeaseIndeterminateException(message, innerException),
            PantsErrorCode.LeaseEpochExhausted => new PantsLeaseEpochExhaustedException(message, innerException),
            PantsErrorCode.WriteConflict => new PantsWriteConflictException(message, innerException),
            PantsErrorCode.Aborted => new PantsAbortedException(message, innerException),
            PantsErrorCode.Busy => new PantsBusyException(message, innerException),
            PantsErrorCode.Timeout => new PantsTimeoutException(message, innerException),
            PantsErrorCode.ResourceLimit => new PantsResourceLimitException(message, innerException),
            _ => new PantsInternalException($"Unknown Pants error code '{code}': {message}", innerException)
        };

    internal static PantsInvalidArgumentException InvalidArgument(string message) => new(message);

    internal static PantsResourceLimitException ResourceLimit(string message) => new(message);

    internal static PantsException FromIOException(IOException exception)
    {
        int nativeCode = exception.HResult & 0xffff;
        string message = exception.Message;
        string normalized = message.ToUpperInvariant();
        return nativeCode is 28 or 112 ||
            normalized.Contains("NO SPACE", StringComparison.Ordinal) ||
            normalized.Contains("DISK FULL", StringComparison.Ordinal)
                ? new PantsNoSpaceException(message, exception)
                : new PantsIOException(message, exception);
    }
}
