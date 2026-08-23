namespace Cntryl.Pants;

public sealed class PantsBusyException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.Busy, message, innerException);
