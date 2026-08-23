namespace Cntryl.Pants.Storage;

public sealed record PantsSstReadTrace(
    string Name,
    uint Level,
    PantsSstReadTier Tier,
    PantsBloomFilterOutcome BloomFilterOutcome,
    PantsCacheReadOutcome ReaderCacheOutcome,
    PantsCacheReadOutcome BlockCacheOutcome,
    int DataBlocksRead);
