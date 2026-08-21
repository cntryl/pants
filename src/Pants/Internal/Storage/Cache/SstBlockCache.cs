namespace Pants;

internal sealed class SstBlockCache
{
    private readonly PantsBlockCachePolicy _policy;
    private readonly long _capacityBytes;
    private readonly Dictionary<SstBlockCacheKey, CacheEntry> _entries = [];
    private readonly Dictionary<SstBlockCacheKey, long> _frequencies = [];
    private readonly LinkedList<SstBlockCacheKey> _recency = [];
    private readonly object _gate = new();
    private LinkedListNode<SstBlockCacheKey>? _clockHand;
    private long _usedBytes;
    private long _accessSequence;

    public SstBlockCache(PantsBlockCachePolicy policy, long capacityBytes)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        _policy = policy;
        _capacityBytes = capacityBytes;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool TryGet(SstBlockCacheKey key)
    {
        lock (_gate)
        {
            long frequency = RecordFrequency(key);
            if (!_entries.TryGetValue(key, out CacheEntry? entry))
            {
                return false;
            }

            entry.Frequency = frequency;
            entry.LastAccess = checked(++_accessSequence);
            entry.Referenced = true;
            if (_policy is PantsBlockCachePolicy.Lru or PantsBlockCachePolicy.TinyLfu)
            {
                _recency.Remove(entry.Node);
                _recency.AddLast(entry.Node);
            }

            return true;
        }
    }

    public void Add(SstBlockCacheKey key, int sizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        lock (_gate)
        {
            long frequency = _frequencies.GetValueOrDefault(key);
            if (frequency == 0)
            {
                frequency = RecordFrequency(key);
            }

            if (_capacityBytes == 0 || sizeBytes > _capacityBytes || _entries.ContainsKey(key))
            {
                return;
            }

            if (_policy == PantsBlockCachePolicy.TinyLfu &&
                _usedBytes + sizeBytes > _capacityBytes)
            {
                CacheEntry victim = _entries.Values
                    .OrderBy(static entry => entry.Frequency)
                    .ThenBy(static entry => entry.LastAccess)
                    .First();
                if (frequency <= victim.Frequency)
                {
                    return;
                }
            }

            while (_usedBytes + sizeBytes > _capacityBytes)
            {
                EvictOne();
            }

            LinkedListNode<SstBlockCacheKey> node = _recency.AddLast(key);
            _entries.Add(key, new CacheEntry(
                node,
                sizeBytes,
                frequency,
                checked(++_accessSequence)));
            _usedBytes = checked(_usedBytes + sizeBytes);
            _clockHand ??= node;
        }
    }

    public void RemoveFile(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        lock (_gate)
        {
            foreach (SstBlockCacheKey key in _entries.Keys
                         .Where(key => StringComparer.Ordinal.Equals(key.FileName, fileName))
                         .ToArray())
            {
                Remove(_entries[key].Node);
            }

            foreach (SstBlockCacheKey key in _frequencies.Keys
                         .Where(key => StringComparer.Ordinal.Equals(key.FileName, fileName))
                         .ToArray())
            {
                _frequencies.Remove(key);
            }
        }
    }

    private long RecordFrequency(SstBlockCacheKey key)
    {
        long frequency = checked(_frequencies.GetValueOrDefault(key) + 1);
        _frequencies[key] = frequency;
        return frequency;
    }

    private void EvictOne()
    {
        if (_entries.Count == 0)
        {
            throw new PantsInternalException("The block cache cannot evict from an empty cache.");
        }

        if (_policy == PantsBlockCachePolicy.ClockPro)
        {
            EvictClockEntry();
            return;
        }

        LinkedListNode<SstBlockCacheKey> node = _recency.First!;
        Remove(node);
    }

    private void EvictClockEntry()
    {
        while (true)
        {
            LinkedListNode<SstBlockCacheKey> node = _clockHand ?? _recency.First!;
            CacheEntry entry = _entries[node.Value];
            _clockHand = node.Next ?? _recency.First;
            if (entry.Referenced)
            {
                entry.Referenced = false;
                continue;
            }

            Remove(node);
            return;
        }
    }

    private void Remove(LinkedListNode<SstBlockCacheKey> node)
    {
        CacheEntry entry = _entries[node.Value];
        if (ReferenceEquals(_clockHand, node))
        {
            _clockHand = node.Next ?? _recency.First;
            if (ReferenceEquals(_clockHand, node))
            {
                _clockHand = null;
            }
        }

        _entries.Remove(node.Value);
        _recency.Remove(node);
        _usedBytes -= entry.SizeBytes;
    }

    private sealed class CacheEntry(
        LinkedListNode<SstBlockCacheKey> node,
        int sizeBytes,
        long frequency,
        long lastAccess)
    {
        public LinkedListNode<SstBlockCacheKey> Node { get; } = node;

        public int SizeBytes { get; } = sizeBytes;

        public long Frequency { get; set; } = frequency;

        public long LastAccess { get; set; } = lastAccess;

        public bool Referenced { get; set; } = true;
    }
}
