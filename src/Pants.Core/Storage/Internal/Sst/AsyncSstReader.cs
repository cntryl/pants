using System.Buffers.Binary;
namespace Cntryl.Pants.Storage.Internal.Sst;

sealed class AsyncSstReader : IAsyncDisposable
{
    readonly byte[]? _blockBlooms;
    readonly long _fileLength;
    readonly (byte[] FirstKey, SstBlockHandle Handle)[] _index;
    readonly IAsyncSstSource _source;
    readonly SharedSource _shared;
    readonly TrieIndex? _trieIndex;
    int _disposed;

    AsyncSstReader(
        IAsyncSstSource source,
        long fileLength,
        (byte[] FirstKey, SstBlockHandle Handle)[] index,
        byte[]? blockBlooms,
        TrieIndex? trieIndex,
        IReadOnlyList<RangeTombstone> rangeTombstones,
        byte[]? smallestKey,
        byte[]? largestKey,
        SharedSource? shared = null)
    {
        _shared = shared ?? new SharedSource(source);
        _source = source;
        _fileLength = fileLength;
        _index = index;
        _blockBlooms = blockBlooms;
        _trieIndex = trieIndex;
        RangeTombstones = rangeTombstones;
        SmallestKey = smallestKey;
        LargestKey = largestKey;
    }

    public int DataBlockCount => _index.Length;

    public IReadOnlyList<RangeTombstone> RangeTombstones { get; }

    public byte[]? SmallestKey { get; }

    public byte[]? LargestKey { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 &&
            Interlocked.Decrement(ref _shared.References) == 0)
        {
            // Keep the slot reserved if close fails; the source may still own a live handle.
            await _source.DisposeAsync().ConfigureAwait(false);
            _shared.ReleaseHandleReservation();
            _shared.MetadataAllocation?.Dispose();
        }
    }

    public void AttachHandleReservation(IDisposable reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ThrowIfDisposed();
        _shared.AttachHandleReservation(reservation);
    }

    /// <summary>
    ///     Approximate heap bytes of the parsed index, blooms, trie and range tombstones.
    /// </summary>
    public long EstimatedMetadataBytes
    {
        get
        {
            const int fixedOverhead = 512;
            const int perIndexEntryOverhead = 56;
            long bytes = fixedOverhead + (_blockBlooms?.Length ?? 0) +
                         (SmallestKey?.Length ?? 0) + (LargestKey?.Length ?? 0);
            foreach (var (firstKey, _) in _index)
            {
                bytes += firstKey.Length + perIndexEntryOverhead;
            }

            if (_trieIndex is not null)
            {
                bytes += _trieIndex.EstimatedMetadataBytes;
            }

            foreach (var tombstone in RangeTombstones)
            {
                bytes += tombstone.Start.Length + tombstone.End.Length + 128;
            }

            return bytes;
        }
    }

    /// <summary>
    ///     Approximate metadata retained by an async scan source after it releases this reader:
    ///     its key bounds and range tombstones. The parsed index, blooms and trie remain owned by
    ///     the reader cache and are accounted by that cache's byte budget.
    /// </summary>
    public long EstimatedScanMetadataBytes
    {
        get
        {
            const int fixedOverhead = 128;
            const int perRangeTombstoneOverhead = 64;
            long bytes = fixedOverhead + (SmallestKey?.Length ?? 0) + (LargestKey?.Length ?? 0);
            foreach (var tombstone in RangeTombstones)
            {
                bytes += tombstone.Start.Length + tombstone.End.Length + perRangeTombstoneOverhead;
            }

            return bytes;
        }
    }

    /// <summary>
    ///     Returns another handle onto the same parsed metadata and source. Each handle is
    ///     disposed on its own; the source closes when the last one is.
    /// </summary>
    public AsyncSstReader Share()
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _shared.References);
        return new AsyncSstReader(
            _source,
            _fileLength,
            _index,
            _blockBlooms,
            _trieIndex,
            RangeTombstones,
            SmallestKey,
            LargestKey,
            _shared);
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
            throw new PantsCorruptionException("SST block index is invalid.");
        }

        return _index[blockIndex].FirstKey.ToArray();
    }

    public static async ValueTask<AsyncSstReader> OpenAsync(
        IAsyncSstSource source,
        FileMeta file,
        CancellationToken cancellationToken,
        ResourceBudget? metadataBudget = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(file);
        SstMetadataAllocation? allocation = null;
        try
        {
            allocation = metadataBudget is null ? null : new SstMetadataAllocation(metadataBudget, file.Name);
            var fileLength = source.Length;
            if (file.SizeBytes != 0 && checked((ulong)fileLength) != file.SizeBytes)
            {
                throw new PantsCorruptionException(
                    $"SST '{file.Name}' length differs from its manifest.");
            }

            if (fileLength < DiskFormat.SstFooterSize)
            {
                if (fileLength >= sizeof(ulong))
                {
                    var trailingMagic = await source.ReadExactlyAsync(
                            fileLength - sizeof(ulong), sizeof(ulong), cancellationToken)
                        .ConfigureAwait(false);
                    if (BinaryPrimitives.ReadUInt64LittleEndian(trailingMagic) == DiskFormat.SstFooterMagic)
                    {
                        throw new PantsCompatibilityException("SST matches a legacy footer magic.");
                    }
                }

                throw new PantsCorruptionException("SST is shorter than its V5 footer.");
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
                await ReadMetadataBlockAsync(source, fileLength, metadataHandle, allocation, cancellationToken)
                    .ConfigureAwait(false));
            SstManifestMetadataValidator.ValidateMetadata(metadata, file, "SST");
            long retainedMetadataBytes = 512 + (metadata.SmallestKey?.Length ?? 0) + (metadata.LargestKey?.Length ?? 0);
            allocation?.Retain(retainedMetadataBytes);

            var index = SstCodec.DecodeIndex(
                    await ReadMetadataBlockAsync(source, fileLength, indexHandle, allocation, cancellationToken)
                        .ConfigureAwait(false))
                .ToArray();
            retainedMetadataBytes = checked(retainedMetadataBytes + index.Sum(static item => item.FirstKey.Length + 56L));
            allocation?.Retain(retainedMetadataBytes);
            var blockBlooms = bloomHandle is { } bloom
                ? await ReadMetadataBlockAsync(source, fileLength, bloom, allocation, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            if (blockBlooms is not null)
            {
                SstCodec.ValidateBlockBlooms(blockBlooms, index.Length);
                retainedMetadataBytes = checked(retainedMetadataBytes + blockBlooms.Length);
                allocation?.Retain(retainedMetadataBytes);
            }

            var trie = trieHandle is { } trieBlock
                ? await ReadMetadataBlockAsync(source, fileLength, trieBlock, allocation, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            var trieIndex = SstCodec.DecodeTrieIndex(metadata.IndexKind, trie, index, allocation);
            retainedMetadataBytes = checked(retainedMetadataBytes + (trieIndex?.EstimatedMetadataBytes ?? 0));
            allocation?.Retain(retainedMetadataBytes);
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
                    await ReadMetadataBlockAsync(source, fileLength, rangeHandle, allocation, cancellationToken)
                        .ConfigureAwait(false))
                : [];
            var reader = new AsyncSstReader(
                source,
                fileLength,
                index,
                blockBlooms,
                trieIndex,
                rangeTombstones,
                metadata.SmallestKey,
                metadata.LargestKey);
            allocation?.Retain(reader.EstimatedMetadataBytes);
            reader._shared.MetadataAllocation = allocation;
            return reader;
        }
        catch (Exception openException)
        {
            try
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw new AsyncSstSourceCleanupException(openException, cleanupException);
            }

            allocation?.Dispose();
            throw;
        }
    }

    public bool HasExclusiveReference => Volatile.Read(ref _shared.References) == 1;

    public bool HasMetadataReservation => _shared.MetadataAllocation is not null;

    static ValueTask<byte[]> ReadMetadataBlockAsync(
        IAsyncSstSource source, long length, SstBlockHandle handle, SstMetadataAllocation? allocation,
        CancellationToken cancellationToken) =>
        allocation is null
            ? ReadBlockAsync(source, length, handle, cancellationToken)
            : allocation.ReadBlockAsync(source, length, handle, cancellationToken);

    public SstPointReadDecision GetPointReadDecision(ReadOnlySpan<byte> key)
    {
        ThrowIfDisposed();
        if (_index.Length == 0)
        {
            return new SstPointReadDecision(0, 0, 0, false, -1, 0);
        }

        var candidate = _trieIndex?.FindFloorBlock(key) ?? SstCodec.FindFloorBlock(_index, key);
        if (candidate < 0)
        {
            return new SstPointReadDecision(0, 0, 0, false, -1, 0);
        }

        var mightContain = _blockBlooms is null || SstCodec.BloomMightContain(_blockBlooms, candidate, key);
        var bloomChecks = _blockBlooms is null ? 0 : 1;
        var handle = _index[candidate].Handle;
        var blockSizeBytes = checked((int)handle.Size);
        return mightContain
            ? new SstPointReadDecision(bloomChecks, 1, 1, false, candidate, blockSizeBytes)
            : new SstPointReadDecision(1, 1, 0, true, candidate, blockSizeBytes);
    }

    public async ValueTask<byte[]> ReadDataBlockAsync(
        int blockIndex,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if ((uint)blockIndex >= (uint)_index.Length)
        {
            throw new PantsCorruptionException("SST point-read block index is invalid.");
        }

        SstCodec.ValidateDataBlockSize(_index[blockIndex].Handle);
        return await ReadBlockAsync(
                _source,
                _fileLength,
                _index[blockIndex].Handle,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<SstReadBlock> ReadDataBlockOwnedAsync(
        int blockIndex, ResourceBudget? resourceBudget, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if ((uint)blockIndex >= (uint)_index.Length)
        {
            throw new PantsCorruptionException("SST block index is invalid.");
        }
        var handle = _index[blockIndex].Handle;
        SstCodec.ValidateDataBlockSize(handle);
        return SstReadBlock.ReadAsync(_source, _fileLength, handle, resourceBudget, cancellationToken);
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
            throw new PantsCorruptionException("SST block handle is outside the file.");
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

    sealed class SharedSource(IAsyncSstSource source)
    {
        public IAsyncSstSource Source { get; } = source;

        public int References = 1;
        public SstMetadataAllocation? MetadataAllocation;

        IDisposable? _handleReservation;

        public void AttachHandleReservation(IDisposable reservation)
        {
            if (Interlocked.CompareExchange(ref _handleReservation, reservation, null) is not null)
            {
                throw new InvalidOperationException(
                    "An async SST source handle reservation is already attached.");
            }
        }

        public void ReleaseHandleReservation() =>
            Interlocked.Exchange(ref _handleReservation, null)?.Dispose();
    }
}
