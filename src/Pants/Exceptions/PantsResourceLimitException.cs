namespace Pants;

public sealed class PantsResourceLimitException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.ResourceLimit, message, innerException);
