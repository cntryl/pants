namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

/// <summary>
///     Reads a remote-only input a block at a time through ranged reads. The merge is synchronous,
///     so each step waits on the range it needs; nothing is hydrated to local disk.
/// </summary>
sealed class RemoteFileCursor : ICompactionFileCursor
{
    readonly AsyncSstBlockIterator _iterator;
    readonly AsyncSstReader _reader;

    public RemoteFileCursor(AsyncSstReader reader, ResourceBudget? resourceBudget)
    {
        _reader = reader;
        _iterator = new AsyncSstBlockIterator(
            reader,
            PantsScanDirection.Forward,
            null,
            null,
            resourceBudget);
    }

    public IReadOnlyList<RangeTombstone> RangeTombstones => _reader.RangeTombstones;

    public SstEntry Current => _iterator.Current;

    public bool MoveNext() =>
        _iterator.MoveNextAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public void Dispose() => _iterator.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
