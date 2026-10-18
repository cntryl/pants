namespace Cntryl.Pants.Exceptions;

public sealed class PantsTimeoutException : PantsException
{
    public PantsTimeoutException(string message, Exception? innerException = null)
        : base(PantsErrorCode.Timeout, message, innerException)
    {
    }
}
