namespace Cntryl.Pants.Exceptions;

public sealed class PantsLeaseUnavailableException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.LeaseUnavailable, message, innerException);
