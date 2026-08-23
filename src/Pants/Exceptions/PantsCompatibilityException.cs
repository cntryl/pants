namespace Cntryl.Pants.Exceptions;

public sealed class PantsCompatibilityException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.CompatibilityError, message, innerException);
