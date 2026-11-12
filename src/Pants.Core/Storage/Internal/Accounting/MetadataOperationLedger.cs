namespace Cntryl.Pants.Storage.Internal.Accounting;

sealed class MetadataOperationLedger(MetadataAccountingState state)
{
    readonly Lock _gate = new();
    readonly ulong[] _counters = new ulong[MetadataAccountingArithmetic.CounterCount];
    bool _sealed;
    bool _overflow;

    public void Mutation()
    {
        lock (_gate)
        {
            if (_sealed)
            {
                state.Invalidate(true);
            }
        }
    }

    public void Add(MetadataCounter counter, ulong value)
    {
        lock (_gate)
        {
            Mutation();
            _overflow |= !MetadataAccountingArithmetic.Add(ref _counters[(int)counter], value);
        }
    }

    public void Set(MetadataCounter counter, ulong value)
    {
        lock (_gate)
        {
            Mutation();
            _counters[(int)counter] = value;
        }
    }

    public (ulong[] Counters, bool Overflow)? Seal(MetadataOperationKind kind, ulong elapsed,
        bool succeeded, bool abandoned, bool overflow)
    {
        lock (_gate)
        {
            if (_sealed)
            {
                return null;
            }

            _sealed = true;
            _counters[(int)MetadataCounter.OperationAttempts] = 1;
            _counters[(int)MetadataCounter.OperationFailures] = succeeded ? 0UL : 1;
            _counters[(int)MetadataCounter.AbandonedOperations] = abandoned ? 1UL : 0;
            if (kind == MetadataOperationKind.Checkpoint)
            {
                _counters[(int)MetadataCounter.CheckpointAttempts] = 1;
                _counters[(int)MetadataCounter.CheckpointNanoseconds] = elapsed;
            }
            else if (kind == MetadataOperationKind.JournalAppend)
            {
                _counters[(int)MetadataCounter.JournalAppendAttempts] = 1;
                _counters[(int)MetadataCounter.JournalNanoseconds] = elapsed;
            }

            return ((ulong[])_counters.Clone(), _overflow || overflow);
        }
    }
}
