namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

/// <summary>
///     Walks key-ordered, non-overlapping files as one stream, holding at most one file open and
///     opening the next only when the current one is exhausted — so a span of any length costs one
///     cursor, not one per file.
/// </summary>
sealed class ChainedFileEntryStream(
    IReadOnlyList<FileMeta> files,
    Func<FileMeta, ICompactionFileCursor> openFile) : ICompactionEntryStream
{
    int _nextFile;
    ICompactionFileCursor? _cursor;

    public SstEntry Current => _cursor?.Current ??
                               throw new InvalidOperationException("The stream has not advanced.");

    public bool MoveNext()
    {
        while (true)
        {
            if (_cursor is not null)
            {
                if (_cursor.MoveNext())
                {
                    return true;
                }

                CloseCurrent();
            }

            if (_nextFile >= files.Count)
            {
                return false;
            }

            _cursor = openFile(files[_nextFile++]);
        }
    }

    public void Dispose() => CloseCurrent();

    void CloseCurrent()
    {
        _cursor?.Dispose();
        _cursor = null;
    }
}
