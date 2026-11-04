namespace Cntryl.Pants.Storage.Internal.Accounting;

sealed class MetadataAccountingOperation : IDisposable
{
    readonly MetadataAccountingState _state;
    readonly TimeProvider _timeProvider;
    readonly long _started;
    readonly MetadataOperationKind _kind;
    readonly int _bucket;

    public MetadataAccountingOperation(MetadataAccountingState state, TimeProvider timeProvider,
        MetadataOperationKind kind, MetadataOrigin origin, MetadataMedium medium)
    {
        _state = state;
        _timeProvider = timeProvider;
        _kind = kind;
        _bucket = (int)origin * 2 + (int)medium;
        _started = timeProvider.GetTimestamp();
        Ledger = new MetadataOperationLedger(state);
        state.Begin(_bucket);
    }

    public MetadataOperationLedger Ledger { get; }

    public MetadataIoObservation Observe(MetadataPayload payload) => new(Ledger, payload);

    public void SnapshotDurable(ulong bytes)
    {
        Ledger.Set(MetadataCounter.SnapshotDurableBytes, bytes);
        Ledger.Set(MetadataCounter.SnapshotDurableCount, 1);
    }

    public void JournalDurable(ulong bytes) => Ledger.Set(MetadataCounter.JournalDurableBytes, bytes);
    public void CheckpointComplete() => Ledger.Set(MetadataCounter.CheckpointCompleteCount, 1);

    public void Finish(bool succeeded) => Finish(succeeded, false);
    public void Dispose() => Finish(false, true);

    public void Execute(Action action)
    {
        try
        {
            action();
            Finish(true);
        }
        catch
        {
            Finish(false);
            throw;
        }
    }

    void Finish(bool succeeded, bool abandoned)
    {
        var elapsed = MetadataAccountingArithmetic.Nanoseconds(_timeProvider.GetElapsedTime(_started), out var valid);
        var counters = Ledger.Seal(_kind, elapsed, succeeded, abandoned, !valid);
        if (counters is { } completed)
        {
            _state.Fold(_bucket, completed.Counters, completed.Overflow, true);
        }
    }
}
