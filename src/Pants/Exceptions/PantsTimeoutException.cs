namespace Cntryl.Pants;

public sealed class PantsTimeoutException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.Timeout, message, innerException);
