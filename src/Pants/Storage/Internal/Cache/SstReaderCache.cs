using System.Collections.Concurrent;

namespace Cntryl.Pants;

internal sealed class SstReaderCache : IDisposable
{
    private readonly ConcurrentDictionary<string, MidgeSstReader> _readers =
        new(StringComparer.Ordinal);
    private int _disposed;

    public MidgeSstReader GetOrAdd(string fileName, string path, out bool cacheHit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_readers.TryGetValue(fileName, out MidgeSstReader? cached))
        {
            cacheHit = true;
            return cached;
        }

        MidgeSstReader created = MidgeSstReader.Open(path);
        if (_readers.TryAdd(fileName, created))
        {
            cacheHit = false;
            return created;
        }

        created.Dispose();
        cacheHit = true;
        return _readers[fileName];
    }

    public void RemoveFile(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (_readers.TryRemove(fileName, out MidgeSstReader? reader))
        {
            reader.Dispose();
        }
    }

    public IReadOnlyList<string> SnapshotFiles() =>
        _readers.Keys.Order(StringComparer.Ordinal).ToArray();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach ((string fileName, MidgeSstReader _) in _readers)
        {
            RemoveFile(fileName);
        }
    }
}
