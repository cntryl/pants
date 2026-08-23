namespace Cntryl.Pants.Exceptions;

public sealed class PantsCorruptionException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.Corruption, message, innerException);
