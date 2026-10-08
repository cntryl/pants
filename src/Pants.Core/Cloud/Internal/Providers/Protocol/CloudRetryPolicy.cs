using System.Net;

namespace Cntryl.Pants.Cloud.Internal.Providers.Protocol;

/// <summary>
///     The read-retry policy shared by every provider: four attempts, exponential backoff of
///     50 ms doubling up to 800 ms, and a fixed set of transient statuses.
/// </summary>
/// <remarks>
///     Other 5xx statuses (501, 505 and similar) mean the request itself is unsupported, so
///     retrying cannot help and they are reported at once. Only reads are ever retried; a mutation's
///     outcome after a failure is indeterminate and is never replayed.
/// </remarks>
static class CloudRetryPolicy
{
    public const int MaximumAttempts = 4;

    static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(50);

    public static bool IsTransientStatus(HttpStatusCode statusCode) =>
        (int)statusCode is 408 or 425 or 429 or 500 or 502 or 503 or 504;

    /// <summary>Delay after failed attempt n: 50 ms * 2^(n-1), growing at most four doublings.</summary>
    public static TimeSpan Backoff(int failedAttempt) =>
        BaseDelay * (1 << Math.Min(Math.Max(failedAttempt - 1, 0), 4));

    /// <summary>
    ///     Sleeps for the backoff, bounded by the operation deadline. When the deadline (and not the
    ///     caller) ends the wait the operation has timed out, which is reported as such.
    /// </summary>
    public static async ValueTask DelayAsync(
        int failedAttempt,
        string provider,
        CancellationToken operationToken,
        CancellationToken callerToken)
    {
        try
        {
            await Task.Delay(Backoff(failedAttempt), operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested)
        {
            throw new PantsTimeoutException($"{provider} operation exceeded its deadline.", exception);
        }
    }
}
