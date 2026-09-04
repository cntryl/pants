namespace Cntryl.Pants.Storage.Internal.Sst;

/// <summary>
///     Lazy read-time range-tombstone masking for SST-sourced entries: a persisted
///     <see cref="RangeTombstone" /> (start inclusive, end exclusive) covers an entry when the
///     tombstone's sequence is newer than the entry's own write sequence — mirroring the eager
///     per-key rewrite <c>Actor.ApplyOperation</c> still performs for in-memory state, but without
///     requiring every covered key to be materialized. Consulted by both point reads
///     (<c>LocalDiskStore.TryReadPointValue</c>) and scans (<c>TransactionScanEnumerator</c>).
/// </summary>
static class SstRangeTombstoneMask
{
    public static bool Covers(
        IReadOnlyList<RangeTombstone> tombstones,
        ReadOnlySpan<byte> key,
        ulong entrySequence)
    {
        foreach (var tombstone in tombstones)
        {
            if (tombstone.Sequence > entrySequence &&
                key.SequenceCompareTo(tombstone.Start) >= 0 &&
                key.SequenceCompareTo(tombstone.End) < 0)
            {
                return true;
            }
        }

        return false;
    }
}
