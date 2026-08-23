namespace Cntryl.Pants.Storage.Internal.Cache.Policy;

sealed class TinyLfuSstBlockCachePolicy : ISstBlockCachePolicy
{
    const int WindowSize = 100;
    readonly Dictionary<SstBlockCacheKey, uint> _frequencies = [];
    readonly LinkedList<SstBlockCacheKey> _recentSamples = [];

    public void RecordAccess(SstBlockCacheKey key)
    {
        _recentSamples.AddLast(key);
        _frequencies[key] = _frequencies.GetValueOrDefault(key) switch
        {
            uint.MaxValue => uint.MaxValue,
            uint frequency => frequency + 1
        };
        if (_recentSamples.Count <= WindowSize)
        {
            return;
        }

        var expired = _recentSamples.First!.Value;
        _recentSamples.RemoveFirst();
        if (!_recentSamples.Contains(expired))
        {
            _frequencies.Remove(expired);
        }
    }

    public bool TrySelectVictim(out SstBlockCacheKey key)
    {
        var sample = _recentSamples.First;
        if (sample is null)
        {
            key = default;
            return false;
        }

        key = sample.Value;
        var minimumFrequency = _frequencies.GetValueOrDefault(key);
        for (sample = sample.Next; sample is not null; sample = sample.Next)
        {
            var frequency = _frequencies.GetValueOrDefault(sample.Value);
            if (frequency < minimumFrequency)
            {
                key = sample.Value;
                minimumFrequency = frequency;
            }
        }

        return true;
    }

    public void RecordRemoval(SstBlockCacheKey key) => Remove(key);

    public void RecordStale(SstBlockCacheKey key) => Remove(key);

    public void Clear()
    {
        _frequencies.Clear();
        _recentSamples.Clear();
    }

    void Remove(SstBlockCacheKey key)
    {
        var node = _recentSamples.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value == key)
            {
                _recentSamples.Remove(node);
            }

            node = next;
        }

        _frequencies.Remove(key);
    }
}
