namespace Cntryl.Pants.Runtime.Internal.Flush;

sealed class ImmutableMemtableFlush(FrozenMemtableFlush frozen)
{
    TaskCompletionSource<Exception?> _attemptCompletion = CreateCompletion();

    public FrozenMemtableFlush Frozen { get; } = frozen;

    public Task<Exception?> AttemptTask => _attemptCompletion.Task;

    public int Attempts { get; private set; }

    public bool IsRunning { get; private set; }

    public bool HasFailed { get; private set; }

    public FlushPublicationPlan? PublicationPlan { get; set; }

    public bool PersistenceAnomaly { get; set; }

    public Task? RunningTask { get; private set; }

    public void BeginAttempt()
    {
        if (IsRunning)
        {
            throw new PantsInternalException(
                $"Immutable flush {Frozen.Id} already has a running attempt.");
        }

        if (Attempts != 0)
        {
            _attemptCompletion = CreateCompletion();
        }

        Attempts = checked(Attempts + 1);
        IsRunning = true;
        HasFailed = false;
        RunningTask = null;
    }

    public void CompleteAttempt(Exception? failure)
    {
        IsRunning = false;
        HasFailed = failure is not null;
        _attemptCompletion.TrySetResult(failure);
        RunningTask = null;
    }

    public void AttachRunningTask(Task runningTask) =>
        RunningTask = runningTask ?? throw new ArgumentNullException(nameof(runningTask));

    public void FailWaiterForShutdown() => _attemptCompletion.TrySetResult(
        new PantsBusyException("The runtime is shutting down."));

    static TaskCompletionSource<Exception?> CreateCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
