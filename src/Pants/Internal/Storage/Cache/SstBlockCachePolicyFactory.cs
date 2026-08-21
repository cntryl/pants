namespace Pants;

internal static class SstBlockCachePolicyFactory
{
    public static ISstBlockCachePolicy Create(PantsBlockCachePolicy policy) => policy switch
    {
        PantsBlockCachePolicy.Lru => new LruSstBlockCachePolicy(),
        PantsBlockCachePolicy.TinyLfu => new TinyLfuSstBlockCachePolicy(),
        PantsBlockCachePolicy.ClockPro => new ClockProSstBlockCachePolicy(),
        _ => throw new PantsInternalException($"Unknown block-cache policy '{policy}'.")
    };
}
