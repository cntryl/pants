namespace Cntryl.Pants.Exceptions;

public sealed class PantsWriteStallException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.WriteStall, message, innerException);
