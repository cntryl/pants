namespace Pants;

sealed class ImmutableMemtableFlush(FrozenMemtableFlush frozen)
{
    TaskCompletionSource<Exception?> _attemptCompletion = CreateCompletion();
    Task? _runningTask;
    int _attempts;
    bool _isRunning;
    bool _hasFailed;

    public FrozenMemtableFlush Frozen { get; } = frozen;

    public Task<Exception?> AttemptTask => _attemptCompletion.Task;

    public int Attempts => _attempts;

    public bool IsRunning => _isRunning;

    public bool HasFailed => _hasFailed;

    public FlushPublicationPlan? PublicationPlan { get; set; }

    public bool PersistenceAnomaly { get; set; }

    public Task? RunningTask => _runningTask;

    public void BeginAttempt()
    {
        if (_isRunning)
        {
            throw new PantsInternalException(
                $"Immutable flush {Frozen.Id} already has a running attempt.");
        }

        if (_attempts != 0)
        {
            _attemptCompletion = CreateCompletion();
        }

        _attempts = checked(_attempts + 1);
        _isRunning = true;
        _hasFailed = false;
        _runningTask = null;
    }

    public void CompleteAttempt(Exception? failure)
    {
        _isRunning = false;
        _hasFailed = failure is not null;
        _attemptCompletion.TrySetResult(failure);
        _runningTask = null;
    }

    public void AttachRunningTask(Task runningTask) =>
        _runningTask = runningTask ?? throw new ArgumentNullException(nameof(runningTask));

    public void FailWaiterForShutdown() => _attemptCompletion.TrySetResult(
        new PantsBusyException("The runtime is shutting down."));

    static TaskCompletionSource<Exception?> CreateCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
