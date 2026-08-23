namespace Cntryl.Pants;

internal sealed class PantsStorageException : PantsIOException
{
    public PantsStorageException(string message)
        : base(message)
    {
    }

    public PantsStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
