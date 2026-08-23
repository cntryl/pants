namespace Cntryl.Pants;

public sealed class PantsNotSupportedException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.NotSupported, message, innerException);
