namespace Cntryl.Pants.Storage.Internal.Sst;

sealed class AsyncSstReader : IAsyncDisposable
{
    readonly byte[]? _blockBlooms;
    readonly long _fileLength;
    readonly (byte[] FirstKey, SstBlockHandle Handle)[] _index;
    readonly IAsyncSstSource _source;
    readonly TrieIndex? _trieIndex;
    int _disposed;

    AsyncSstReader(
        IAsyncSstSource source,
        long fileLength,
        (byte[] FirstKey, SstBlockHandle Handle)[] index,
        byte[]? blockBlooms,
        TrieIndex? trieIndex,
        IReadOnlyList<RangeTombstone> rangeTombstones)
    {
        _source = source;
        _fileLength = fileLength;
        _index = index;
        _blockBlooms = blockBlooms;
        _trieIndex = trieIndex;
        RangeTombstones = rangeTombstones;
    }

    public int DataBlockCount => _index.Length;

    public IReadOnlyList<RangeTombstone> RangeTombstones { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _source.DisposeAsync().ConfigureAwait(false);
        }
    }

    public int CountOverlappingDataBlocks(byte[]? startInclusive, byte[]? endExclusive)
    {
        ThrowIfDisposed();
        var count = 0;
        for (var blockIndex = 0; blockIndex < _index.Length; blockIndex++)
        {
            var firstKey = _index[blockIndex].FirstKey;
            var nextFirstKey = blockIndex + 1 < _index.Length
                ? _index[blockIndex + 1].FirstKey
                : null;
            if ((endExclusive is null || firstKey.AsSpan().SequenceCompareTo(endExclusive) < 0) &&
                (startInclusive is null || nextFirstKey is null ||
                 nextFirstKey.AsSpan().SequenceCompareTo(startInclusive) > 0))
            {
                count++;
            }
        }

        return count;
    }

    public byte[] GetFirstKey(int blockIndex)
    {
        ThrowIfDisposed();
        if ((uint)blockIndex >= (uint)_index.Length)
        {
            throw new StorageException("SST block index is invalid.");
        }

        return _index[blockIndex].FirstKey.ToArray();
    }

    public static async ValueTask<AsyncSstReader> OpenAsync(
        IAsyncSstSource source,
        FileMeta file,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(file);
        try
        {
            var fileLength = source.Length;
            if (file.SizeBytes != 0 && checked((ulong)fileLength) != file.SizeBytes)
            {
                throw new PantsCorruptionException(
                    $"SST '{file.Name}' length differs from its manifest.");
            }

            if (fileLength < DiskFormat.SstFooterSize)
            {
                throw new PantsCorruptionException("SST is shorter than its V4 footer.");
            }

            var footerOffset = fileLength - DiskFormat.SstFooterSize;
            var footer = await source.ReadExactlyAsync(
                    footerOffset,
                    DiskFormat.SstFooterSize,
                    cancellationToken)
                .ConfigureAwait(false);
            SstCodec.ValidateFooter(footer);
            var metadataHandle = SstCodec.ReadHandle(footer, 0);
            var indexHandle = SstCodec.ReadHandle(footer, 16);
            var trieHandle = SstCodec.ReadOptionalHandle(footer, 32, "trie");
            var bloomHandle = SstCodec.ReadOptionalHandle(footer, 48, "block bloom");
            var metadata = SstCodec.DecodeMetadata(
                await ReadBlockAsync(source, fileLength, metadataHandle, cancellationToken)
                    .ConfigureAwait(false));
            SstManifestMetadataValidator.ValidateMetadata(metadata, file, "SST");
            var index = SstCodec.DecodeIndex(
                    await ReadBlockAsync(source, fileLength, indexHandle, cancellationToken)
                        .ConfigureAwait(false))
                .ToArray();
            var blockBlooms = bloomHandle is { } bloom
                ? await ReadBlockAsync(source, fileLength, bloom, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            if (blockBlooms is not null)
            {
                SstCodec.ValidateBlockBlooms(blockBlooms, index.Length);
            }

            var trie = trieHandle is { } trieBlock
                ? await ReadBlockAsync(source, fileLength, trieBlock, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            var trieIndex = SstCodec.DecodeTrieIndex(metadata.IndexKind, trie, index);
            SstCodec.ValidateBlockCoverage(
                footerOffset,
                metadataHandle,
                indexHandle,
                trieHandle,
                bloomHandle,
                metadata.RangeHandle,
                index.ToList());
            var rangeTombstones = metadata.RangeHandle is { } rangeHandle
                ? SstCodec.DecodeRangeTombstones(
                    await ReadBlockAsync(source, fileLength, rangeHandle, cancellationToken)
                        .ConfigureAwait(false))
                : [];
            return new AsyncSstReader(
                source,
                fileLength,
                index,
                blockBlooms,
                trieIndex,
                rangeTombstones);
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public SstPointReadDecision GetPointReadDecision(ReadOnlySpan<byte> key)
    {
        ThrowIfDisposed();
        if (_index.Length == 0 || _blockBlooms is null)
        {
            return new SstPointReadDecision(0, 0, 0, false, -1, 0);
        }

        var candidate = _trieIndex?.FindFloorBlock(key) ?? SstCodec.FindFloorBlock(_index, key);
        if (candidate < 0)
        {
            return new SstPointReadDecision(0, 0, 0, false, -1, 0);
        }

        var mightContain = SstCodec.BloomMightContain(_blockBlooms, candidate, key);
        var handle = _index[candidate].Handle;
        var blockSizeBytes = checked((int)handle.Size);
        return mightContain
            ? new SstPointReadDecision(1, 1, 1, false, candidate, blockSizeBytes)
            : new SstPointReadDecision(1, 1, 0, true, candidate, blockSizeBytes);
    }

    public async ValueTask<byte[]> ReadDataBlockAsync(
        int blockIndex,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if ((uint)blockIndex >= (uint)_index.Length)
        {
            throw new StorageException("SST point-read block index is invalid.");
        }

        return await ReadBlockAsync(
                _source,
                _fileLength,
                _index[blockIndex].Handle,
                cancellationToken)
            .ConfigureAwait(false);
    }

    static async ValueTask<byte[]> ReadBlockAsync(
        IAsyncSstSource source,
        long fileLength,
        SstBlockHandle handle,
        CancellationToken cancellationToken)
    {
        if (handle.Offset > (ulong)fileLength ||
            handle.Size < 9 ||
            handle.Size > int.MaxValue ||
            handle.Size > (ulong)fileLength - handle.Offset)
        {
            throw new StorageException("SST block handle is outside the file.");
        }

        var encoded = await source.ReadExactlyAsync(
                checked((long)handle.Offset),
                checked((int)handle.Size),
                cancellationToken)
            .ConfigureAwait(false);
        return SstCodec.ReadBlock(encoded, new SstBlockHandle(0, handle.Size));
    }

    void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
