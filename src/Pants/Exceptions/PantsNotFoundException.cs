namespace Cntryl.Pants.Exceptions;

public sealed class PantsNotFoundException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.NotFound, message, innerException);
