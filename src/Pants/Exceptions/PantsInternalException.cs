namespace Cntryl.Pants.Exceptions;

public sealed class PantsInternalException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.Internal, message, innerException);
