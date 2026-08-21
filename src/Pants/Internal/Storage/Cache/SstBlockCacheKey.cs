namespace Pants;

internal readonly record struct SstBlockCacheKey(string FileName, int BlockIndex);
