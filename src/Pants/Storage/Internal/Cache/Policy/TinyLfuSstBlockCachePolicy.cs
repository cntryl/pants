namespace Cntryl.Pants.Storage.Internal.Cache.Policy;

sealed class TinyLfuSstBlockCachePolicy : ISstBlockCachePolicy
{
    internal const int WindowSize = 100;
    readonly Dictionary<SstBlockCacheKey, FrequencyEntry> _frequencies = [];
    ulong _nextOrder;

    public void RecordAccess(SstBlockCacheKey key)
    {
        if (_frequencies.TryGetValue(key, out var existing))
        {
            _frequencies[key] = existing with
            {
                Frequency = existing.Frequency == uint.MaxValue
                    ? uint.MaxValue
                    : existing.Frequency + 1
            };
            return;
        }

        _frequencies.Add(key, new FrequencyEntry(1, _nextOrder));
        _nextOrder = _nextOrder == ulong.MaxValue ? 0 : _nextOrder + 1;
    }

    public bool TrySelectVictim(out SstBlockCacheKey key)
    {
        using var candidates = _frequencies.GetEnumerator();
        if (!candidates.MoveNext())
        {
            key = default;
            return false;
        }

        key = candidates.Current.Key;
        var minimum = candidates.Current.Value;
        while (candidates.MoveNext())
        {
            var candidate = candidates.Current.Value;
            if (candidate.Frequency < minimum.Frequency ||
                (candidate.Frequency == minimum.Frequency && candidate.Order < minimum.Order))
            {
                key = candidates.Current.Key;
                minimum = candidate;
            }
        }

        return true;
    }

    public void RecordRemoval(SstBlockCacheKey key) => Remove(key);

    public void RecordStale(SstBlockCacheKey key) => Remove(key);

    public void Clear()
    {
        _frequencies.Clear();
        _nextOrder = 0;
    }

    void Remove(SstBlockCacheKey key) => _frequencies.Remove(key);

    readonly record struct FrequencyEntry(uint Frequency, ulong Order);
}
