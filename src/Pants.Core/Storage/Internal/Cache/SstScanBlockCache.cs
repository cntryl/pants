namespace Cntryl.Pants.Storage.Internal.Cache;

sealed class SstScanBlockCache(
    SstBlockCache cache,
    SstFileIdentity file,
    PantsCacheFill fill)
{
    public bool TryGet(int blockIndex, out ReadOnlyMemory<byte> content)
    {
        if (cache.TryGet(new SstBlockCacheKey(file, blockIndex), out var entry) && entry is not null)
        {
            content = entry.Content;
            return true;
        }

        content = default;
        return false;
    }

    public void Admit(int blockIndex, ReadOnlySpan<byte> content)
    {
        if (fill == PantsCacheFill.Normal)
        {
            _ = cache.Add(new SstBlockCacheKey(file, blockIndex), content);
        }
    }
}
