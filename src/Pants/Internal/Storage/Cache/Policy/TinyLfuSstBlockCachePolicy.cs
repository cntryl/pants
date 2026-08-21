namespace Pants;

internal sealed class TinyLfuSstBlockCachePolicy : ISstBlockCachePolicy
{
    private const int WindowSize = 100;
    private readonly Dictionary<SstBlockCacheKey, uint> _frequencies = [];
    private readonly LinkedList<SstBlockCacheKey> _recentSamples = [];

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

        SstBlockCacheKey expired = _recentSamples.First!.Value;
        _recentSamples.RemoveFirst();
        if (!_recentSamples.Contains(expired))
        {
            _frequencies.Remove(expired);
        }
    }

    public bool TrySelectVictim(out SstBlockCacheKey key)
    {
        LinkedListNode<SstBlockCacheKey>? sample = _recentSamples.First;
        if (sample is null)
        {
            key = default;
            return false;
        }

        key = sample.Value;
        uint minimumFrequency = _frequencies.GetValueOrDefault(key);
        for (sample = sample.Next; sample is not null; sample = sample.Next)
        {
            uint frequency = _frequencies.GetValueOrDefault(sample.Value);
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

    private void Remove(SstBlockCacheKey key)
    {
        LinkedListNode<SstBlockCacheKey>? node = _recentSamples.First;
        while (node is not null)
        {
            LinkedListNode<SstBlockCacheKey>? next = node.Next;
            if (node.Value == key)
            {
                _recentSamples.Remove(node);
            }

            node = next;
        }

        _frequencies.Remove(key);
    }
}
