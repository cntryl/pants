namespace Cntryl.Pants;

public sealed class PantsLeaseHeldException(string message, Exception? innerException = null)
    : PantsException(PantsErrorCode.LeaseHeld, message, innerException);
