namespace Cntryl.Pants.Observability.Internal;

/// <summary>One open's observation lifetime, explicitly shared only with its recovery work.</summary>
sealed class RecoveryWorkSink : IDisposable
{
    readonly Lock _gate = new();
    readonly TimeProvider _timeProvider;
    Action<RecoveryWorkCompleted>? _completed;
    bool _closed;

    public RecoveryWorkSink(Action<RecoveryWorkCompleted>? completed = null, TimeProvider? timeProvider = null)
    {
        _completed = completed;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public RecoveryWorkRecorder? Begin(string stage)
    {
        lock (_gate)
        {
            return !_closed && (_completed is not null ||
                                PantsDiagnostics.RecoveryCompletedBytes.Enabled ||
                                PantsDiagnostics.RecoveryCompletedFrames.Enabled ||
                                PantsDiagnostics.RecoveryCompletedOperations.Enabled)
                ? new RecoveryWorkRecorder(this, stage, _timeProvider)
                : null;
        }
    }

    public void Record(RecoveryWorkCompleted work)
    {
        if (!work.HasCompletedWork)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            var stage = new KeyValuePair<string, object?>("pants.recovery.stage", work.Stage);
            if (work.Bytes > 0)
            {
                PantsDiagnostics.RecoveryCompletedBytes.Add(work.Bytes, stage);
            }
            if (work.Frames > 0)
            {
                PantsDiagnostics.RecoveryCompletedFrames.Add(work.Frames, stage);
            }
            if (work.Operations > 0)
            {
                PantsDiagnostics.RecoveryCompletedOperations.Add(work.Operations, stage);
            }

            _completed?.Invoke(work);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            _completed = null;
        }
    }
}
