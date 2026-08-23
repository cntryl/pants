namespace Cntryl.Pants.Storage.Internal.Cache.Policy;

sealed class ClockProSstBlockCacheSlot
{
    public SstBlockCacheKey? Key { get; set; }

    public bool Referenced { get; set; }

    public bool Hot { get; set; }
}
