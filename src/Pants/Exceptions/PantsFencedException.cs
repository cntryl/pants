namespace Cntryl.Pants;

public sealed class PantsFencedException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.Fenced, message, innerException);
