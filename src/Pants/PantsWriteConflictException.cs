namespace Pants;

public sealed class PantsWriteConflictException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.WriteConflict, message, innerException);
