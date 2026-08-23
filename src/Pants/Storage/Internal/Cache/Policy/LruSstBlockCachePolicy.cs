namespace Cntryl.Pants.Storage.Internal.Cache.Policy;

sealed class LruSstBlockCachePolicy : ISstBlockCachePolicy
{
    readonly Dictionary<SstBlockCacheKey, LinkedListNode<SstBlockCacheKey>> _nodes = [];
    readonly LinkedList<SstBlockCacheKey> _recency = [];

    public void RecordAccess(SstBlockCacheKey key)
    {
        if (_nodes.Remove(key, out var existing))
        {
            _recency.Remove(existing);
        }

        _nodes.Add(key, _recency.AddLast(key));
    }

    public bool TrySelectVictim(out SstBlockCacheKey key)
    {
        if (_recency.First is null)
        {
            key = default;
            return false;
        }

        key = _recency.First.Value;
        return true;
    }

    public void RecordRemoval(SstBlockCacheKey key) => Remove(key);

    public void RecordStale(SstBlockCacheKey key) => Remove(key);

    public void Clear()
    {
        _nodes.Clear();
        _recency.Clear();
    }

    void Remove(SstBlockCacheKey key)
    {
        if (_nodes.Remove(key, out var node))
        {
            _recency.Remove(node);
        }
    }
}
