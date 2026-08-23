namespace Pants;

public sealed class PantsCompatibilityException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.CompatibilityError, message, innerException);
