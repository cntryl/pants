namespace Cntryl.Pants.Storage.Internal.Sst;

interface IAsyncSstEvictionVerifier
{
    ValueTask VerifyRemoteSstForEvictionAsync(
        FileMeta file,
        long localLength,
        CancellationToken cancellationToken);
}
