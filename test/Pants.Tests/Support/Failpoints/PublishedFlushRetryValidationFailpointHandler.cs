namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class PublishedFlushRetryValidationFailpointHandler :
    IPantsFailpointHandler,
    IDisposable
{
    static readonly TimeSpan MaximumBlockTime = TimeSpan.FromSeconds(10);
    readonly ManualResetEventSlim _release = new(false);

    readonly TaskCompletionSource _retryValidationEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    int _publicationFailureInjected;
    int _retryValidationBlocked;

    public void Dispose()
    {
        _release.Set();
        _release.Dispose();
    }

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint == PantsFailpoint.AfterFlushManifestPublish &&
            Interlocked.CompareExchange(ref _publicationFailureInjected, 1, 0) == 0)
        {
            throw new IOException($"Injected failure at {failpoint}.");
        }

        if (failpoint != PantsFailpoint.BeforePublishedFlushRetryValidation ||
            Interlocked.CompareExchange(ref _retryValidationBlocked, 1, 0) != 0)
        {
            return;
        }

        _retryValidationEntered.TrySetResult();
        if (!_release.Wait(MaximumBlockTime))
        {
            throw new TimeoutException($"Timed out waiting to release {failpoint}.");
        }
    }

    public async Task WaitForRetryValidationAsync(TimeSpan timeout) =>
        await _retryValidationEntered.Task.WaitAsync(timeout);

    public void Release() => _release.Set();
}
