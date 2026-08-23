namespace Cntryl.Pants.Storage.Internal.Cache;

readonly record struct SstBlockCacheKey(string FileName, int BlockIndex);
