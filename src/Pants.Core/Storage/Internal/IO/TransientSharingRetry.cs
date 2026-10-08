namespace Cntryl.Pants.Storage.Internal.IO;

/// <summary>
///     Retries a rename or delete that Windows refused only because another handle briefly holds
///     the file.
/// </summary>
/// <remarks>
///     Windows rejects replacing or deleting a file while any handle without delete sharing is
///     open on it, and antivirus or indexing services open freshly written files on their own
///     schedule. Those refusals surface as access-denied or sharing/lock violations and clear
///     on their own. The operations retried here are all-or-nothing, so a refused attempt left
///     nothing behind and repeating it is safe. Other platforms do not report these errors
///     transiently, so they are never retried there.
/// </remarks>
static class TransientSharingRetry
{
    internal const int MaximumAttempts = 20;
    const int ErrorSharingViolation = 32;
    const int ErrorLockViolation = 33;
    static readonly TimeSpan MaximumDelay = TimeSpan.FromMilliseconds(100);

    public static void Move(string source, string destination, bool overwrite) =>
        Run(() => File.Move(source, destination, overwrite));

    public static void Delete(string path) => Run(() => File.Delete(path));

    public static void Run(Action operation) =>
        Run(operation, OperatingSystem.IsWindows(), Thread.Sleep);

    internal static void Run(Action operation, bool retryTransientFailures, Action<TimeSpan> delay)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(delay);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception exception) when (
                retryTransientFailures &&
                attempt < MaximumAttempts &&
                IsTransient(exception))
            {
                delay(GetDelay(attempt));
            }
        }
    }

    static bool IsTransient(Exception exception) =>
        exception is UnauthorizedAccessException ||
        (exception is IOException && (exception.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation);

    static TimeSpan GetDelay(int attempt)
    {
        var milliseconds = 1L << Math.Min(attempt - 1, 7);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaximumDelay.TotalMilliseconds));
    }
}
