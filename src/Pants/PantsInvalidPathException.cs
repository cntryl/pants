namespace Pants;

public sealed class PantsInvalidPathException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.InvalidPath, message, innerException);
