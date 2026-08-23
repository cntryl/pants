namespace Cntryl.Pants;

public sealed class PantsInvalidArgumentException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.InvalidArgument, message, innerException);
