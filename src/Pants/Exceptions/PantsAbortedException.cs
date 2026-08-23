namespace Cntryl.Pants;

public sealed class PantsAbortedException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.Aborted, message, innerException);
