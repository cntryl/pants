namespace Cntryl.Pants;

internal sealed class ClockProSstBlockCacheSlot
{
    public SstBlockCacheKey? Key { get; set; }

    public bool Referenced { get; set; }

    public bool Hot { get; set; }
}
