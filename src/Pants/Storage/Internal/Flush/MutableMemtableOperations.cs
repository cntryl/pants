namespace Cntryl.Pants.Storage.Internal.Flush;

sealed class MutableMemtableOperations
{
    readonly Dictionary<uint, List<WalMutation>> _families = [];
    int _count;

    public int Count => _count;

    public ulong LastSequence => _families.Count == 0
        ? 0
        : _families.Values.Max(static family => family[^1].Sequence);

    public void Add(WalMutation operation)
    {
        if (!_families.TryGetValue(operation.ColumnFamilyId, out var family))
        {
            family = [];
            _families.Add(operation.ColumnFamilyId, family);
        }

        family.Add(operation);
        _count = checked(_count + 1);
    }

    public void AddRange(IEnumerable<WalMutation> operations)
    {
        foreach (var operation in operations)
        {
            Add(operation);
        }
    }

    public IReadOnlyList<WalMutation> DetachFamily(uint columnFamilyId)
    {
        if (!_families.Remove(columnFamilyId, out var family))
        {
            return [];
        }

        _count -= family.Count;
        return family;
    }

    public IReadOnlyList<WalMutation> SnapshotAll() => _families.Values
        .SelectMany(static family => family)
        .OrderBy(static operation => operation.Sequence)
        .ToArray();

    public void TruncateAfter(ulong sequence)
    {
        foreach (var pair in _families.ToArray())
        {
            var family = pair.Value;
            var retainedCount = family.FindLastIndex(operation => operation.Sequence <= sequence) + 1;
            if (retainedCount == family.Count)
            {
                continue;
            }

            _count -= family.Count - retainedCount;
            family.RemoveRange(retainedCount, family.Count - retainedCount);
            if (retainedCount == 0)
            {
                _families.Remove(pair.Key);
            }
        }
    }

    public void Clear()
    {
        _families.Clear();
        _count = 0;
    }
}
