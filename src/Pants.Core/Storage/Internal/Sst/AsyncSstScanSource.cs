namespace Cntryl.Pants.Storage.Internal.Sst;

/// <summary>
///     One SST's contribution to a scan merge. Its metadata is read when the scan starts, but the
///     reader is released straight away and reacquired only when the merge reaches the file, so a
///     scan over many files holds handles for the files it is actually reading rather than all of
///     them.
/// </summary>
sealed class AsyncSstScanSource : IAsyncDisposable
{
    readonly PantsScanDirection _direction;
    readonly byte[]? _endExclusive;
    readonly Func<CancellationToken, ValueTask<AsyncSstReader>> _reopenReader;
    readonly ResourceBudget? _resourceBudget;
    readonly byte[]? _startInclusive;
    int _dataBlocksRead;
    AsyncSstBlockIterator? _iterator;

    AsyncSstScanSource(
        FileMeta file,
        AsyncSstReader reader,
        Func<CancellationToken, ValueTask<AsyncSstReader>> reopenReader,
        PantsScanDirection direction,
        byte[]? startInclusive,
        byte[]? endExclusive,
        ResourceBudget? resourceBudget)
    {
        _reopenReader = reopenReader;
        _direction = direction;
        _startInclusive = startInclusive;
        _endExclusive = endExclusive;
        _resourceBudget = resourceBudget;
        SmallestKey = reader.SmallestKey ??
                      throw new PantsCorruptionException($"SST '{file.Name}' has no smallest key.");
        LargestKey = reader.LargestKey ??
                     throw new PantsCorruptionException($"SST '{file.Name}' has no largest key.");
        RangeTombstones = reader.RangeTombstones;
        CandidateBlockCount = reader.CountOverlappingDataBlocks(startInclusive, endExclusive);
    }

    public int CandidateBlockCount { get; }

    public SstEntry Current => _iterator?.Current ??
                               throw new InvalidOperationException("The scan source has not advanced.");

    public int DataBlocksRead => checked(_dataBlocksRead + (_iterator?.DataBlocksRead ?? 0));

    public byte[] SmallestKey { get; }

    public byte[] LargestKey { get; }

    public IReadOnlyList<RangeTombstone> RangeTombstones { get; }

    /// <summary>
    ///     Reads <paramref name="reader" />'s metadata and releases it; the source reopens it
    ///     through <paramref name="reopenReader" /> when the merge first advances it.
    /// </summary>
    public static async ValueTask<AsyncSstScanSource> CreateAsync(
        FileMeta file,
        AsyncSstReader reader,
        Func<CancellationToken, ValueTask<AsyncSstReader>> reopenReader,
        PantsScanDirection direction,
        byte[]? startInclusive,
        byte[]? endExclusive,
        ResourceBudget? resourceBudget)
    {
        try
        {
            return new AsyncSstScanSource(
                file,
                reader,
                reopenReader,
                direction,
                startInclusive,
                endExclusive,
                resourceBudget);
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_iterator is not { } iterator)
        {
            return;
        }

        _dataBlocksRead = checked(_dataBlocksRead + iterator.DataBlocksRead);
        _iterator = null;
        await iterator.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken)
    {
        if (_iterator is null)
        {
            var reader = await _reopenReader(cancellationToken).ConfigureAwait(false);
            _iterator = new AsyncSstBlockIterator(
                reader,
                _direction,
                _startInclusive,
                _endExclusive,
                _resourceBudget);
        }

        return await _iterator.MoveNextAsync(cancellationToken).ConfigureAwait(false);
    }
}
