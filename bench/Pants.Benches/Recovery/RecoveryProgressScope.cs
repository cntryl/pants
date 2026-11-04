using Cntryl.Pants.Observability.Internal;

namespace Cntryl.Pants.Recovery;

sealed class RecoveryProgressScope(
    RecoveryProgressTracker owner,
    RecoveryProgressScope? previous,
    long generation,
    bool recovery,
    long startedAt) : IDisposable
{
    internal RecoveryProgressScope? Previous { get; } = previous;

    internal long Generation { get; } = generation;

    internal bool IsRecovery { get; } = recovery;

    internal bool Closed { get; set; }

    internal long ProgressEvents { get; set; }

    internal long LastCompletedAt { get; set; } = startedAt;

    public long CompletedEvents => owner.Read(this).CompletedEvents;

    public TimeSpan IdleFor => owner.Read(this).IdleFor;

    public void Record(RecoveryWorkCompleted work) => owner.Record(this, work);

    public void Dispose() => owner.Close(this);
}
