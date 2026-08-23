namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class DegradedWriteStallFailpointHandler : IFailpointHandler, IDisposable
{
    static readonly TimeSpan MaximumBlockTime = TimeSpan.FromSeconds(10);

    readonly TaskCompletionSource _flushEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    readonly ManualResetEventSlim _flushRelease = new(false);
    int _checkpointArmed;
    int _checkpointHit;
    int _flushArmed;
    int _flushHit;

    public void Dispose()
    {
        _flushRelease.Set();
        _flushRelease.Dispose();
    }

    public void Hit(Failpoint failpoint)
    {
        if (failpoint == Failpoint.BeforeManifestCheckpointReplace &&
            Volatile.Read(ref _checkpointArmed) != 0 &&
            Interlocked.CompareExchange(ref _checkpointHit, 1, 0) == 0)
        {
            throw new IOException($"Injected failure at {failpoint}.");
        }

        if (failpoint != Failpoint.BeforeFlushManifestPublish ||
            Volatile.Read(ref _flushArmed) == 0 ||
            Interlocked.CompareExchange(ref _flushHit, 1, 0) != 0)
        {
            return;
        }

        _flushEntered.TrySetResult();
        if (!_flushRelease.Wait(MaximumBlockTime))
        {
            throw new TimeoutException($"Timed out waiting to release {failpoint}.");
        }
    }

    public void FailNextCheckpoint() => Volatile.Write(ref _checkpointArmed, 1);

    public void BlockNextFlushPublication() => Volatile.Write(ref _flushArmed, 1);

    public async Task WaitForFlushAsync(TimeSpan timeout) =>
        await _flushEntered.Task.WaitAsync(timeout);

    public void ReleaseFlush() => _flushRelease.Set();
}
