using Cntryl.Pants.Observability.Internal;

namespace Cntryl.Pants.Recovery;

/// <summary>Consumer policy for one workload's caller and phase; no engine resource is retained.</summary>
sealed class RecoveryProgressTracker(TimeProvider? timeProvider = null)
{
    readonly Lock _gate = new();
    readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    RecoveryProgressScope? _current;
    long _generation;
    long _completedEvents;

    public long CompletedEvents
    {
        get
        {
            lock (_gate)
            {
                return _completedEvents;
            }
        }
    }

    public RecoveryProgressScope EnterRecovery() => Enter(true);

    public RecoveryProgressScope EnterWorkload() => Enter(false);

    public void AdvancePhase()
    {
        lock (_gate)
        {
            _generation = checked(_generation + 1);
        }
    }

    RecoveryProgressScope Enter(bool recovery)
    {
        lock (_gate)
        {
            var scope = new RecoveryProgressScope(this, _current, _generation, recovery, _time.GetTimestamp());
            _current = scope;
            return scope;
        }
    }

    internal void Record(RecoveryProgressScope scope, RecoveryWorkCompleted work)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, scope) || scope.Closed || !scope.IsRecovery ||
                scope.Generation != _generation || !work.HasCompletedWork)
            {
                return;
            }

            _completedEvents = checked(_completedEvents + 1);
            scope.ProgressEvents = checked(scope.ProgressEvents + 1);
            scope.LastCompletedAt = _time.GetTimestamp();
        }
    }

    internal (long CompletedEvents, TimeSpan IdleFor) Read(RecoveryProgressScope scope)
    {
        lock (_gate)
        {
            return (scope.ProgressEvents, _time.GetElapsedTime(scope.LastCompletedAt));
        }
    }

    internal void Close(RecoveryProgressScope scope)
    {
        lock (_gate)
        {
            scope.Closed = true;
            if (ReferenceEquals(_current, scope))
            {
                _current = scope.Previous;
                while (_current is { Closed: true })
                {
                    _current = _current.Previous;
                }
            }
        }
    }
}
