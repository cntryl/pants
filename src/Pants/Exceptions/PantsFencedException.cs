namespace Cntryl.Pants.Exceptions;

public sealed class PantsFencedException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.Fenced, message, innerException);
