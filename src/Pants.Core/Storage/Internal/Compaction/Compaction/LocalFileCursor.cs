namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

sealed class LocalFileCursor : ICompactionFileCursor
{
    readonly SstBlockIterator _iterator;
    readonly SstReader _reader;

    public LocalFileCursor(SstReader reader, ResourceBudget? resourceBudget)
    {
        _reader = reader;
        _iterator = SstBlockIterator.Create(
            reader,
            PantsScanDirection.Forward,
            resourceBudget: resourceBudget);
    }

    public IReadOnlyList<RangeTombstone> RangeTombstones => _reader.RangeTombstones;

    public SstEntry Current => _iterator.Current;

    public bool MoveNext() => _iterator.MoveNext();

    public void Dispose()
    {
        _iterator.Dispose();
        _reader.Dispose();
    }
}
