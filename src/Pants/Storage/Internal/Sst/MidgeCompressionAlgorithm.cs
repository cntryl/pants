namespace Cntryl.Pants;

internal enum MidgeCompressionAlgorithm : byte
{
    None = 0,
    Lz4 = 1,
    Zstd3 = 2,
    Zstd9 = 3
}
