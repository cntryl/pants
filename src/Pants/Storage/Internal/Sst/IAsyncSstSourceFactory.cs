namespace Cntryl.Pants.Storage.Internal.Sst;

interface IAsyncSstSourceFactory
{
    ValueTask<IAsyncSstSource?> OpenAsync(
        FileMeta file,
        CancellationToken cancellationToken);
}
