namespace Cntryl.Pants.Support.TestDoubles;

sealed class ControllableSstBlockCachePolicy : ISstBlockCachePolicy
{
    readonly LinkedList<SstBlockCacheKey> _keys = [];

    public ManualResetEventSlim AccessEntered { get; } = new();

    public ManualResetEventSlim ReleaseAccess { get; } = new();

    public bool PauseAccess { get; set; }

    public void RecordAccess(SstBlockCacheKey key)
    {
        if (PauseAccess)
        {
            AccessEntered.Set();
            ReleaseAccess.Wait();
        }

        _keys.Remove(key);
        _keys.AddLast(key);
    }

    public bool TrySelectVictim(out SstBlockCacheKey key)
    {
        if (_keys.First is null)
        {
            key = default;
            return false;
        }

        key = _keys.First.Value;
        return true;
    }

    public void RecordRemoval(SstBlockCacheKey key) => _keys.Remove(key);

    public void RecordStale(SstBlockCacheKey key) => _keys.Remove(key);

    public void Clear() => _keys.Clear();
}
