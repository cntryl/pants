using System.Diagnostics;

namespace Cntryl.Pants.Runtime.Internal;

sealed class StartupPhaseRecorder
{
    readonly Action<StartupPhaseMeasurement>? _record;

    public StartupPhaseRecorder(Action<StartupPhaseMeasurement>? record)
    {
        _record = record;
    }

    public Scope Measure(StartupPhase phase) => _record is null
        ? default
        : new Scope(this, phase);

    public readonly struct Scope : IDisposable
    {
        readonly long _allocatedBytes;
        readonly StartupPhase _phase;
        readonly StartupPhaseRecorder? _recorder;
        readonly long _timestamp;

        internal Scope(StartupPhaseRecorder recorder, StartupPhase phase)
        {
            _recorder = recorder;
            _phase = phase;
            _timestamp = Stopwatch.GetTimestamp();
            _allocatedBytes = GC.GetTotalAllocatedBytes();
        }

        public void Dispose()
        {
            if (_recorder?._record is not { } record)
            {
                return;
            }

            record(new StartupPhaseMeasurement(
                _phase,
                Stopwatch.GetElapsedTime(_timestamp),
                GC.GetTotalAllocatedBytes() - _allocatedBytes));
        }
    }
}
