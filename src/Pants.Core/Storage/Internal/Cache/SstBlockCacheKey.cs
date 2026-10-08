namespace Cntryl.Pants.Storage.Internal.Cache;

readonly record struct SstBlockCacheKey(SstFileIdentity File, int BlockIndex)
{
    public string FileName => File.Name;
}
