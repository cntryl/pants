using System.Collections.Concurrent;

namespace Pants;

internal sealed class SstBlockCacheShard
{
    private readonly ConcurrentDictionary<SstBlockCacheKey, SstBlockCacheEntry> _entries = [];
    private readonly ISstBlockCachePolicy _policy;
    private readonly long _capacityBytes;
    private readonly object _mutationGate = new();
    private long _usedBytes;

    public SstBlockCacheShard(long capacityBytes, ISstBlockCachePolicy policy)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        _capacityBytes = capacityBytes;
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public int Count => _entries.Count;

    public long UsedBytes => Volatile.Read(ref _usedBytes);

    public bool TryGet(SstBlockCacheKey key, out SstBlockCacheEntry? entry)
    {
        if (!_entries.ContainsKey(key))
        {
            entry = null;
            return false;
        }

        lock (_mutationGate)
        {
            if (!_entries.TryGetValue(key, out SstBlockCacheEntry? current))
            {
                entry = null;
                return false;
            }

            _policy.RecordAccess(key);
            entry = current;
            return true;
        }
    }

    public bool Add(SstBlockCacheKey key, ReadOnlySpan<byte> content)
    {
        if (_capacityBytes == 0 || content.IsEmpty || content.Length > _capacityBytes)
        {
            return false;
        }

        byte[] ownedContent = content.ToArray();
        lock (_mutationGate)
        {
            if (_entries.ContainsKey(key))
            {
                _policy.RecordAccess(key);
                return true;
            }

            var entry = new SstBlockCacheEntry(ownedContent);
            if (!_entries.TryAdd(key, entry))
            {
                _policy.RecordAccess(key);
                return true;
            }

            _usedBytes = checked(_usedBytes + entry.SizeBytes);
            _policy.RecordAccess(key);
            while (_usedBytes > _capacityBytes)
            {
                if (!TryEvictOne())
                {
                    RemoveCore(key);
                    return false;
                }
            }

            return _entries.ContainsKey(key);
        }
    }

    public void RemoveFile(string fileName)
    {
        lock (_mutationGate)
        {
            foreach (SstBlockCacheKey key in _entries.Keys.Where(
                         key => StringComparer.Ordinal.Equals(key.FileName, fileName)))
            {
                RemoveCore(key);
            }
        }
    }

    public IReadOnlyList<SstBlockCacheKey> SnapshotKeys() =>
        _entries.Keys.OrderBy(static key => key.FileName, StringComparer.Ordinal)
            .ThenBy(static key => key.BlockIndex)
            .ToArray();

    private bool TryEvictOne()
    {
        int maximumAttempts = checked(_entries.Count + 1);
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            if (!_policy.TrySelectVictim(out SstBlockCacheKey victim))
            {
                return false;
            }

            if (_entries.TryRemove(victim, out SstBlockCacheEntry? removed))
            {
                _usedBytes = checked(_usedBytes - removed.SizeBytes);
                _policy.RecordRemoval(victim);
                return true;
            }

            _policy.RecordStale(victim);
        }

        return false;
    }

    private void RemoveCore(SstBlockCacheKey key)
    {
        if (_entries.TryRemove(key, out SstBlockCacheEntry? removed))
        {
            _usedBytes = checked(_usedBytes - removed.SizeBytes);
        }

        _policy.RecordRemoval(key);
    }
}
