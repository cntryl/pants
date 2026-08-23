namespace Cntryl.Pants.Exceptions;

public class PantsIOException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.Io, message, innerException);
