namespace Cntryl.Pants.Exceptions;

public sealed class PantsTimeoutException : PantsException
{
    public PantsTimeoutException(string message, Exception? innerException = null)
        : this(message, innerException, false)
    {
    }

    internal PantsTimeoutException(
        string message,
        Exception? innerException,
        bool runtimeResponseAbandoned)
        : base(PantsErrorCode.Timeout, message, innerException)
    {
        RuntimeResponseAbandoned = runtimeResponseAbandoned;
    }

    internal bool RuntimeResponseAbandoned { get; }
}
