namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

/// <summary>
///     Walks key-ordered, non-overlapping files as one stream, holding at most one reader open and
///     opening the next only when the current one is exhausted — so a span of any length costs one
///     cursor, not one per file.
/// </summary>
sealed class ChainedFileEntryStream(
    IReadOnlyList<string> paths,
    Func<string, SstReader> openReader,
    ResourceBudget? resourceBudget) : ICompactionEntryStream
{
    int _nextPath;
    SstBlockIterator? _iterator;
    SstReader? _reader;

    public SstEntry Current => _iterator?.Current ??
                               throw new InvalidOperationException("The stream has not advanced.");

    public bool MoveNext()
    {
        while (true)
        {
            if (_iterator is not null)
            {
                if (_iterator.MoveNext())
                {
                    return true;
                }

                CloseCurrent();
            }

            if (_nextPath >= paths.Count)
            {
                return false;
            }

            _reader = openReader(paths[_nextPath++]);
            _iterator = SstBlockIterator.Create(
                _reader,
                PantsScanDirection.Forward,
                resourceBudget: resourceBudget);
        }
    }

    public void Dispose() => CloseCurrent();

    void CloseCurrent()
    {
        _iterator?.Dispose();
        _iterator = null;
        _reader?.Dispose();
        _reader = null;
    }
}
