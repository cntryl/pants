namespace Cntryl.Pants.Storage.Internal.Sst;

sealed class AsyncSstScanSource : IAsyncDisposable
{
    readonly AsyncSstBlockIterator _iterator;

    public AsyncSstScanSource(
        FileMeta file,
        AsyncSstReader reader,
        AsyncSstBlockIterator iterator,
        byte[]? startInclusive,
        byte[]? endExclusive)
    {
        _iterator = iterator;
        SmallestKey = LocalDiskStore.GetMetadataKey(
            file.SmallestKey ?? throw new PantsCorruptionException(
                $"Manifest SST '{file.Name}' has no smallest key."));
        LargestKey = LocalDiskStore.GetMetadataKey(
            file.LargestKey ?? throw new PantsCorruptionException(
                $"Manifest SST '{file.Name}' has no largest key."));
        RangeTombstones = reader.RangeTombstones;
        CandidateBlockCount = reader.CountOverlappingDataBlocks(startInclusive, endExclusive);
    }

    public int CandidateBlockCount { get; }

    public SstEntry Current => _iterator.Current;

    public int DataBlocksRead => _iterator.DataBlocksRead;

    public byte[] SmallestKey { get; }

    public byte[] LargestKey { get; }

    public IReadOnlyList<RangeTombstone> RangeTombstones { get; }

    public ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken) =>
        _iterator.MoveNextAsync(cancellationToken);

    public ValueTask DisposeAsync() => _iterator.DisposeAsync();
}
