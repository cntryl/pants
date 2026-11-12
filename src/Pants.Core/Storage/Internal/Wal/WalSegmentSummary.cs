namespace Cntryl.Pants.Storage.Internal.Wal;

sealed class WalSegmentSummary(WalOpenFacts? open)
{
    public ulong RecordCount { get; private set; }
    public ulong FirstSequence { get; private set; }
    public ulong LastSequence { get; private set; } = open?.Prev.LastSeq ?? 0;
    public SortedDictionary<uint, ulong> MaximumSequenceByFamily { get; } = [];

    public void Observe(WalRecord record)
    {
        if (open is not null)
        {
            WalCodec.ValidateSequenceSpan(record, open.FirstSeq, RecordCount == 0 ? null : LastSequence);
        }

        if (RecordCount == 0)
        {
            FirstSequence = record.Sequence;
        }

        RecordCount = checked(RecordCount + 1);
        LastSequence = record.Sequence;
        MaximumSequenceByFamily[record.ColumnFamilyId] = Math.Max(
            MaximumSequenceByFamily.GetValueOrDefault(record.ColumnFamilyId), record.Sequence);
    }
}
