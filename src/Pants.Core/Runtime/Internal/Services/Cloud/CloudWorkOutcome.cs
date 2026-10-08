namespace Cntryl.Pants.Runtime.Internal.Services.Cloud;

enum CloudWorkOutcome
{
    /// <summary>The requested work is done; run again only when signalled.</summary>
    Completed,

    /// <summary>Work remains and the operation yielded its turn; run again at once.</summary>
    Continue,

    /// <summary>Work remains but cannot progress now; run again after the retry backoff.</summary>
    RetryLater
}
