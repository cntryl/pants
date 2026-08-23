using System.Text;

namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class CoalescedCommitCrashFailpointHandler(
    string sentinelPath,
    int expectedCommitCount) : IPantsFailpointHandler, IDisposable
{
    static readonly TimeSpan MaximumBlockTime = TimeSpan.FromSeconds(30);

    readonly TaskCompletionSource _runtimeBarrierEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    readonly ManualResetEventSlim _runtimeBarrierRelease = new(false);
    int _crashArmed = 1;
    int _runtimeBarrierArmed = 1;

    public void Dispose()
    {
        _runtimeBarrierRelease.Set();
        _runtimeBarrierRelease.Dispose();
    }

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint == PantsFailpoint.BeforeRuntimeMetricsResponse &&
            Interlocked.Exchange(ref _runtimeBarrierArmed, 0) == 1)
        {
            _runtimeBarrierEntered.TrySetResult();
            if (!_runtimeBarrierRelease.Wait(MaximumBlockTime))
            {
                throw new TimeoutException(
                    $"Timed out waiting to release {PantsFailpoint.BeforeRuntimeMetricsResponse}.");
            }

            return;
        }

        if (failpoint != PantsFailpoint.AfterCoalescedWalDurabilityBoundary ||
            Interlocked.Exchange(ref _crashArmed, 0) != 1)
        {
            return;
        }

        var sentinel = Encoding.UTF8.GetBytes(
            $"trigger={PantsFailpoint.AfterCoalescedWalDurabilityBoundary}\n" +
            $"expected-commits={expectedCommitCount}\n");
        using (var stream = new FileStream(
                   sentinelPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.Read,
                   4_096,
                   FileOptions.WriteThrough))
        {
            stream.Write(sentinel);
            stream.Flush(true);
        }

        var parent = Path.GetDirectoryName(sentinelPath) ??
                     throw new InvalidOperationException(
                         "The coalesced-commit crash sentinel has no parent directory.");
        AtomicStagedFile.FlushDirectory(parent);

        Environment.FailFast(
            $"Injected crash at {PantsFailpoint.AfterCoalescedWalDurabilityBoundary}.");
    }

    public async Task WaitForRuntimeBarrierAsync(TimeSpan timeout) =>
        await _runtimeBarrierEntered.Task.WaitAsync(timeout);

    public void ReleaseRuntimeBarrier() => _runtimeBarrierRelease.Set();
}
