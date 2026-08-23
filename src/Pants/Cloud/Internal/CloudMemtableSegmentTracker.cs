namespace Cntryl.Pants;

sealed class CloudMemtableSegmentTracker
{
    readonly Dictionary<ColumnFamilyIdentity, ulong> _startedInSegment =
        new(ColumnFamilyIdentityComparer.Instance);

    public void RecordWrite(ColumnFamilyIdentity family, ulong currentSegmentId) =>
        _startedInSegment.TryAdd(family, currentSegmentId);

    public void RecordFlush(ColumnFamilyIdentity family) => _startedInSegment.Remove(family);

    public void Reinitialize(IEnumerable<ColumnFamilyIdentity> nonemptyFamilies, ulong currentSegmentId)
    {
        _startedInSegment.Clear();
        foreach (var family in nonemptyFamilies)
        {
            _startedInSegment[family] = currentSegmentId;
        }
    }

    public ColumnFamilyIdentity? SelectFlushCandidate(ulong currentSegmentId, ulong segmentGap) =>
        _startedInSegment
            .Select(entry => new
            {
                Family = entry.Key,
                Gap = currentSegmentId - Math.Min(currentSegmentId, entry.Value)
            })
            .Where(candidate => candidate.Gap >= segmentGap)
            .OrderByDescending(static candidate => candidate.Gap)
            .ThenBy(static candidate => candidate.Family.Id)
            .Select(static candidate => (ColumnFamilyIdentity?)candidate.Family)
            .FirstOrDefault();

    public ulong MaximumGap(ulong currentSegmentId) => _startedInSegment
        .Select(entry => currentSegmentId - Math.Min(currentSegmentId, entry.Value))
        .DefaultIfEmpty()
        .Max();
}
