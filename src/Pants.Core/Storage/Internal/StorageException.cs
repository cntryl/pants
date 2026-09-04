namespace Cntryl.Pants.Storage.Internal;

sealed class StorageException : PantsIOException
{
    public StorageException(string message)
        : base(message)
    {
    }

    public StorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
