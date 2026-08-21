namespace Pants;

public sealed class PantsNoSpaceException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.NoSpace, message, innerException);
