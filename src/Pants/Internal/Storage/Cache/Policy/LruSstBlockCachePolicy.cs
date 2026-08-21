namespace Pants;

internal sealed class LruSstBlockCachePolicy : ISstBlockCachePolicy
{
    private readonly Dictionary<SstBlockCacheKey, LinkedListNode<SstBlockCacheKey>> _nodes = [];
    private readonly LinkedList<SstBlockCacheKey> _recency = [];

    public void RecordAccess(SstBlockCacheKey key)
    {
        if (_nodes.Remove(key, out LinkedListNode<SstBlockCacheKey>? existing))
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

    private void Remove(SstBlockCacheKey key)
    {
        if (_nodes.Remove(key, out LinkedListNode<SstBlockCacheKey>? node))
        {
            _recency.Remove(node);
        }
    }
}
