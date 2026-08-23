namespace Cntryl.Pants.Storage.Internal.Sst;

enum CompressionAlgorithm : byte
{
    None = 0,
    Lz4 = 1,
    Zstd3 = 2,
    Zstd9 = 3
}
