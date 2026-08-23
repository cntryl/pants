using System.Collections.Concurrent;

namespace Cntryl.Pants.Storage.Internal.Cache;

sealed class SstReaderCache : IDisposable
{
    readonly ConcurrentDictionary<string, MidgeSstReader> _readers =
        new(StringComparer.Ordinal);

    int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var (fileName, _) in _readers)
        {
            RemoveFile(fileName);
        }
    }

    public MidgeSstReader GetOrAdd(string fileName, string path, out bool cacheHit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_readers.TryGetValue(fileName, out var cached))
        {
            cacheHit = true;
            return cached;
        }

        var created = MidgeSstReader.Open(path);
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
        if (_readers.TryRemove(fileName, out var reader))
        {
            reader.Dispose();
        }
    }

    public IReadOnlyList<string> SnapshotFiles() =>
        _readers.Keys.Order(StringComparer.Ordinal).ToArray();
}
