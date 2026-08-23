namespace Cntryl.Pants.Exceptions;

public sealed class PantsLeaseIndeterminateException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.LeaseIndeterminate, message, innerException);
