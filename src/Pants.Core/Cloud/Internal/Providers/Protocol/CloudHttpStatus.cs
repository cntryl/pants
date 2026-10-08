using System.Net;

namespace Cntryl.Pants.Cloud.Internal.Providers.Protocol;

/// <summary>
///     Builds provider failures that carry the HTTP status as data, so callers classify by status
///     rather than by the text of Pants's own messages. An HTTP 408 is a timeout.
/// </summary>
static class CloudHttpStatus
{
    const string DataKey = "pants.http-status";

    public static PantsException Failure(HttpStatusCode statusCode, string message)
    {
        PantsException exception = statusCode == HttpStatusCode.RequestTimeout
            ? new PantsTimeoutException(message)
            : new PantsIOException(message);
        exception.Data[DataKey] = (int)statusCode;
        return exception;
    }

    /// <summary>The HTTP status a provider failure was built from, if any.</summary>
    public static HttpStatusCode? Of(Exception exception) =>
        exception.Data[DataKey] is int status ? (HttpStatusCode)status : null;
}
