namespace Cntryl.Pants.Support.Failpoints;

/// <summary>
///     Blocks a background flush immediately before it builds its SST — the frozen generation is
///     already tracked in <c>RuntimeState.ImmutableMemtableFlushes</c> at this point (freeze happens
///     before this failpoint fires) and stays there for as long as the flush is blocked, so tests can
///     observe genuinely in-flight immutable-generation state instead of a flush that has already
///     completed and been released by the time metrics are sampled.
/// </summary>
sealed class BlockingFlushBuildFailpointHandler : IFailpointHandler
{
    readonly TaskCompletionSource _blocked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != Failpoint.BeforeFlushBuild)
        {
            return;
        }

        _blocked.TrySetResult();
        _release.Task.GetAwaiter().GetResult();
    }

    public Task WaitUntilBlockedAsync(TimeSpan timeout) => _blocked.Task.WaitAsync(timeout);

    public void Release() => _release.TrySetResult();
}
