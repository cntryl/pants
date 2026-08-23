namespace Cntryl.Pants.Exceptions;

public sealed class PantsRecoveryFailedException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.RecoveryFailed, message, innerException);
