namespace Cntryl.Pants.Exceptions;

public sealed class PantsLeaseEpochExhaustedException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.LeaseEpochExhausted, message, innerException);
