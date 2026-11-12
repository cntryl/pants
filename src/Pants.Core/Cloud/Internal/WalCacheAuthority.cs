namespace Cntryl.Pants.Cloud.Internal;

sealed record WalCacheAuthority(string CacheId, ulong CatalogEpoch, ulong MinimumWriterEpoch);
