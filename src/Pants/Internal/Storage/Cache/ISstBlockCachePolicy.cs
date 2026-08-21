namespace Pants;

/// <summary>
/// Provides eviction hints for one cache shard. Implementations must tolerate removal and stale-key
/// notifications for keys that are no longer represented in policy state.
/// </summary>
internal interface ISstBlockCachePolicy
{
    void RecordAccess(SstBlockCacheKey key);

    bool TrySelectVictim(out SstBlockCacheKey key);

    void RecordRemoval(SstBlockCacheKey key);

    void RecordStale(SstBlockCacheKey key);

    void Clear();
}
