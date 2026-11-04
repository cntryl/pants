namespace Cntryl.Pants.Storage.Internal.Sst;

/// <summary>
///     One SST contribution to a merge. Accounted metadata shares the physical cached reader.
///     Trusted nonoverlapping runs defer opening until the merge reaches their manifest bounds.
/// </summary>
sealed class AsyncSstScanSource : IAsyncDisposable
{
    readonly PantsScanDirection _direction;
    readonly byte[]? _endExclusive;
    readonly Func<CancellationToken, ValueTask<AsyncSstReader>> _reopenReader;
    readonly ResourceBudget? _resourceBudget;
    readonly SstScanBlockCache? _blockCache;
    IDisposable? _readerMetadataReservation;
    AsyncSstReader? _metadataReader;
    bool _exhausted;
    readonly byte[]? _startInclusive;
    int _dataBlocksRead;
    int _blockCacheHits;
    int _blockCacheMisses;
    AsyncSstBlockIterator? _iterator;

    AsyncSstScanSource(
        FileMeta file,
        AsyncSstReader reader,
        Func<CancellationToken, ValueTask<AsyncSstReader>> reopenReader,
        PantsScanDirection direction,
        byte[]? startInclusive,
        byte[]? endExclusive,
        ResourceBudget? resourceBudget,
        IDisposable? readerMetadataReservation,
        SstScanBlockCache? blockCache)
    {
        _reopenReader = reopenReader;
        _direction = direction;
        _startInclusive = startInclusive;
        _endExclusive = endExclusive;
        _resourceBudget = resourceBudget;
        _blockCache = blockCache;
        _readerMetadataReservation = readerMetadataReservation;
        SmallestKey = reader.SmallestKey ??
                      throw new PantsCorruptionException($"SST '{file.Name}' has no smallest key.");
        LargestKey = reader.LargestKey ??
                     throw new PantsCorruptionException($"SST '{file.Name}' has no largest key.");
        RangeTombstones = reader.RangeTombstones;
        CandidateBlockCount = reader.CountOverlappingDataBlocks(startInclusive, endExclusive);
    }

    public int CandidateBlockCount { get; private set; }

    public SstEntry Current => _iterator?.Current ??
                               throw new InvalidOperationException("The scan source has not advanced.");

    public int DataBlocksRead => checked(_dataBlocksRead + (_iterator?.DataBlocksRead ?? 0));

    public int BlockCacheHits => checked(_blockCacheHits + (_iterator?.BlockCacheHits ?? 0));

    public int BlockCacheMisses => checked(_blockCacheMisses + (_iterator?.BlockCacheMisses ?? 0));

    public byte[] SmallestKey { get; }

    public byte[] LargestKey { get; }

    public IReadOnlyList<RangeTombstone> RangeTombstones { get; private set; }

    /// <summary>
    ///     Keeps the accounted reader lease. Standalone readers without an owner retain only
    ///     separately admitted scan metadata and reopen through <paramref name="reopenReader" />.
    /// </summary>
    public static async ValueTask<AsyncSstScanSource> CreateAsync(
        FileMeta file,
        AsyncSstReader reader,
        Func<CancellationToken, ValueTask<AsyncSstReader>> reopenReader,
        PantsScanDirection direction,
        byte[]? startInclusive,
        byte[]? endExclusive,
        ResourceBudget? resourceBudget,
        SstScanBlockCache? blockCache = null)
    {
        IDisposable? readerMetadataReservation = null;
        AsyncSstScanSource? source = null;
        try
        {
            readerMetadataReservation = reader.HasMetadataReservation
                ? null
                : resourceBudget?.Reserve(reader.EstimatedScanMetadataBytes);
            source = new AsyncSstScanSource(
                file,
                reader,
                reopenReader,
                direction,
                startInclusive,
                endExclusive,
                resourceBudget,
                readerMetadataReservation,
                blockCache);
            readerMetadataReservation = null;
            if (reader.HasMetadataReservation)
            {
                source._metadataReader = reader;
            }
            else
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
            return source;
        }
        catch
        {
            readerMetadataReservation?.Dispose();
            if (source is not null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }

            await reader.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    AsyncSstScanSource(
        FileMeta file,
        Func<CancellationToken, ValueTask<AsyncSstReader>> reopenReader,
        PantsScanDirection direction, byte[]? startInclusive, byte[]? endExclusive,
        ResourceBudget? resourceBudget, SstScanBlockCache? blockCache)
    {
        _reopenReader = reopenReader;
        _direction = direction;
        _startInclusive = startInclusive;
        _endExclusive = endExclusive;
        _resourceBudget = resourceBudget;
        _blockCache = blockCache;
        SmallestKey = file.SmallestKey!;
        LargestKey = file.LargestKey!;
        RangeTombstones = [];
    }

    public static AsyncSstScanSource CreateLazy(
        FileMeta file, Func<CancellationToken, ValueTask<AsyncSstReader>> reopenReader,
        PantsScanDirection direction, byte[]? startInclusive, byte[]? endExclusive,
        ResourceBudget? resourceBudget, SstScanBlockCache? blockCache = null) =>
        new(file, reopenReader, direction, startInclusive, endExclusive, resourceBudget, blockCache);

    public async ValueTask DisposeAsync()
    {
        var iterator = _iterator;
        _iterator = null;
        try
        {
            if (iterator is not null)
            {
                _dataBlocksRead = checked(_dataBlocksRead + iterator.DataBlocksRead);
                _blockCacheHits = checked(_blockCacheHits + iterator.BlockCacheHits);
                _blockCacheMisses = checked(_blockCacheMisses + iterator.BlockCacheMisses);
                await iterator.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (_metadataReader is { } metadataReader)
            {
                _metadataReader = null;
                await metadataReader.DisposeAsync().ConfigureAwait(false);
            }
            Interlocked.Exchange(ref _readerMetadataReservation, null)?.Dispose();
        }
    }

    public async ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken)
    {
        if (_exhausted)
        {
            return false;
        }
        if (_iterator is null)
        {
            _metadataReader ??= await _reopenReader(cancellationToken).ConfigureAwait(false);
            var reader = _metadataReader.Share();
            RangeTombstones = reader.RangeTombstones;
            CandidateBlockCount = reader.CountOverlappingDataBlocks(_startInclusive, _endExclusive);
            _iterator = new AsyncSstBlockIterator(
                reader,
                _direction,
                _startInclusive,
                _endExclusive,
                reader.HasMetadataReservation ? null : _resourceBudget,
                _blockCache);
        }

        if (await _iterator.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        _exhausted = true;
        return false;
    }
}
