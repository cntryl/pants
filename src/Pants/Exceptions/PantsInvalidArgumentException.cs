namespace Cntryl.Pants.Exceptions;

public sealed class PantsInvalidArgumentException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.InvalidArgument, message, innerException);
