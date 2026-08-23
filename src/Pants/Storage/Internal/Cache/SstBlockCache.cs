namespace Cntryl.Pants;

internal sealed class SstBlockCache
{
    private const int DefaultShardCount = 16;
    private readonly SstBlockCacheShard[] _shards;

    public SstBlockCache(
        PantsBlockCachePolicy policy,
        long capacityBytes,
        int shardCount = DefaultShardCount,
        Func<int, ISstBlockCachePolicy>? policyFactory = null)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(shardCount);
        int capacityLimitedShardCount = capacityBytes == 0
            ? 1
            : checked((int)Math.Min(capacityBytes, int.MaxValue));
        int resolvedShardCount = Math.Min(shardCount, capacityLimitedShardCount);
        long shardCapacity = capacityBytes / resolvedShardCount;
        _shards = Enumerable.Range(0, resolvedShardCount)
            .Select(index => new SstBlockCacheShard(
                shardCapacity,
                policyFactory?.Invoke(index) ?? SstBlockCachePolicyFactory.Create(policy)))
            .ToArray();
    }

    public int Count => _shards.Sum(static shard => shard.Count);

    public int ShardCount => _shards.Length;

    public long UsedBytes => _shards.Sum(static shard => shard.UsedBytes);

    public bool TryGet(SstBlockCacheKey key, out SstBlockCacheEntry? entry) =>
        GetShard(key).TryGet(key, out entry);

    public bool Add(SstBlockCacheKey key, ReadOnlySpan<byte> content) =>
        GetShard(key).Add(key, content);

    public void RemoveFile(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        foreach (SstBlockCacheShard shard in _shards)
        {
            shard.RemoveFile(fileName);
        }
    }

    public IReadOnlyList<SstBlockCacheKey> SnapshotKeys() =>
        _shards.SelectMany(static shard => shard.SnapshotKeys())
            .OrderBy(static key => key.FileName, StringComparer.Ordinal)
            .ThenBy(static key => key.BlockIndex)
            .ToArray();

    internal int GetShardIndex(SstBlockCacheKey key) =>
        checked((int)(unchecked((uint)key.GetHashCode()) % (uint)_shards.Length));

    private SstBlockCacheShard GetShard(SstBlockCacheKey key) => _shards[GetShardIndex(key)];
}
