namespace Cntryl.Pants.Cloud.Internal.Providers.Protocol;

/// <summary>
///     A response body that ended early or was reset after valid headers. Unlike a protocol
///     violation it is transient, so a read may be retried.
/// </summary>
sealed class CloudBodyReadException(string message, Exception innerException)
    : PantsIOException(message, innerException);
