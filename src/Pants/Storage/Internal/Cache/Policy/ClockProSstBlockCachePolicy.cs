namespace Cntryl.Pants.Storage.Internal.Cache.Policy;

sealed class ClockProSstBlockCachePolicy : ISstBlockCachePolicy
{
    const int DefaultCapacity = 1024;
    readonly List<ClockProSstBlockCacheSlot> _slots = new(DefaultCapacity);
    readonly Dictionary<SstBlockCacheKey, int> _slotsByKey = [];
    int _hand;
    int _hotCount;
    int _hotTarget = DefaultCapacity / 4;

    public void RecordAccess(SstBlockCacheKey key)
    {
        if (_slotsByKey.TryGetValue(key, out var existingIndex))
        {
            var existing = _slots[existingIndex];
            existing.Referenced = true;
            if (!existing.Hot && _hotCount < _hotTarget)
            {
                existing.Hot = true;
                _hotCount++;
            }

            return;
        }

        var slotIndex = _slots.FindIndex(static slot => slot.Key is null);
        if (slotIndex < 0)
        {
            slotIndex = _slots.Count;
            _slots.Add(new ClockProSstBlockCacheSlot());
        }

        var slot = _slots[slotIndex];
        slot.Key = key;
        slot.Referenced = true;
        slot.Hot = false;
        _slotsByKey.Add(key, slotIndex);
    }

    public bool TrySelectVictim(out SstBlockCacheKey key)
    {
        if (_slots.Count == 0)
        {
            key = default;
            return false;
        }

        var maximumScans = checked(_slots.Count + 1);
        for (var scan = 0; scan < maximumScans; scan++)
        {
            var slot = _slots[_hand];
            AdvanceHand();
            if (slot.Key is not { } candidate)
            {
                continue;
            }

            if (slot.Referenced)
            {
                slot.Referenced = false;
                continue;
            }

            if (slot.Hot && _hotCount <= _hotTarget)
            {
                continue;
            }

            key = candidate;
            RemoveSlot(candidate);
            return true;
        }

        key = default;
        return false;
    }

    public void RecordRemoval(SstBlockCacheKey key) => RemoveSlot(key);

    public void RecordStale(SstBlockCacheKey key) => RemoveSlot(key);

    public void Clear()
    {
        _slotsByKey.Clear();
        _slots.Clear();
        _hand = 0;
        _hotCount = 0;
        _hotTarget = DefaultCapacity / 4;
    }

    void RemoveSlot(SstBlockCacheKey key)
    {
        if (!_slotsByKey.Remove(key, out var slotIndex))
        {
            return;
        }

        var slot = _slots[slotIndex];
        if (slot.Hot)
        {
            _hotCount--;
        }

        slot.Key = null;
        slot.Referenced = false;
        slot.Hot = false;
    }

    void AdvanceHand() => _hand = (_hand + 1) % _slots.Count;
}
