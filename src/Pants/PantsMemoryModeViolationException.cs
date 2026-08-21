namespace Pants;

public sealed class PantsMemoryModeViolationException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.MemoryModeViolation, message, innerException);
