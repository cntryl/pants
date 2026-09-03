namespace Cntryl.Pants.Storage.Internal.Sst;

interface IAsyncSstSource : IAsyncDisposable
{
    long Length { get; }

    ValueTask<byte[]> ReadExactlyAsync(
        long offset,
        int length,
        CancellationToken cancellationToken);
}
