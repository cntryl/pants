namespace Cntryl.Pants;

sealed class MidgeWalWriterEpochFrontiers
{
    readonly SortedDictionary<ulong, ulong> _firstSequenceByEpoch = [];
    readonly SortedDictionary<ulong, ulong> _firstOrdinalByEpoch = [];

    public void Record(MidgeWalRecord record, ulong ordinal)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.WriterEpoch == 0)
        {
            return;
        }

        RecordMinimum(_firstSequenceByEpoch, record.WriterEpoch, record.Sequence);
        RecordMinimum(_firstOrdinalByEpoch, record.WriterEpoch, ordinal);
    }

    public bool IsStale(MidgeWalRecord record, ulong ordinal)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.WriterEpoch == 0)
        {
            return false;
        }

        foreach (var (epoch, firstSequence) in _firstSequenceByEpoch)
        {
            if (epoch <= record.WriterEpoch)
            {
                continue;
            }

            if (record.Sequence >= firstSequence ||
                _firstOrdinalByEpoch[epoch] < ordinal)
            {
                return true;
            }
        }

        return false;
    }

    static void RecordMinimum(
        SortedDictionary<ulong, ulong> values,
        ulong epoch,
        ulong candidate)
    {
        if (!values.TryGetValue(epoch, out var current) || candidate < current)
        {
            values[epoch] = candidate;
        }
    }
}
