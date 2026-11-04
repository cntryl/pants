using System.Buffers.Binary;
using Cntryl.Pants.Storage.Internal.IO;
using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants.Storage.Internal.Sst;

sealed class SstReader : IDisposable
{
    readonly byte[]? _blockBlooms;
    readonly SafeFileHandle _file;
    readonly long _fileLength;
    readonly (byte[] FirstKey, SstBlockHandle Handle)[] _index;
    readonly TrieIndex? _trieIndex;
    IDisposable? _handleReservation;
    SstMetadataAllocation? _metadataAllocation;
    int _disposed;

    SstReader(
        SafeFileHandle file,
        long fileLength,
        (byte[] FirstKey, SstBlockHandle Handle)[] index,
        byte[]? blockBlooms,
        TrieIndex? trieIndex,
        IReadOnlyList<RangeTombstone> rangeTombstones,
        IDisposable? handleReservation)
    {
        _file = file;
        _fileLength = fileLength;
        _index = index;
        _blockBlooms = blockBlooms;
        _trieIndex = trieIndex;
        _handleReservation = handleReservation;
        RangeTombstones = rangeTombstones;
    }

    public int DataBlockCount => _index.Length;

    /// <summary>
    ///     Approximate heap bytes of the parsed index, blooms, trie and range tombstones, which is
    ///     what a cached reader costs beyond its file handle.
    /// </summary>
    public long EstimatedMetadataBytes
    {
        get
        {
            const int fixedOverhead = 512;
            const int perIndexEntryOverhead = 56;
            long bytes = fixedOverhead + (_blockBlooms?.Length ?? 0);
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
    ///     The SST's range tombstones. Loaded eagerly by <see cref="Open" /> since a file's
    ///     tombstone set is small and bounded, unlike its data blocks.
    /// </summary>
    public IReadOnlyList<RangeTombstone> RangeTombstones { get; }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _file.Dispose();
            }
            finally
            {
                Interlocked.Exchange(ref _handleReservation, null)?.Dispose();
                Interlocked.Exchange(ref _metadataAllocation, null)?.Dispose();
            }
        }
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

    public SstBlockHandle GetDataBlockHandle(int blockIndex)
    {
        ThrowIfDisposed();
        if ((uint)blockIndex >= (uint)_index.Length)
        {
            throw new PantsCorruptionException("SST block index is invalid.");
        }

        return _index[blockIndex].Handle;
    }

    public static SstReader Open(string path) => OpenCore(path, null);

    internal static SstReader Open(string path, IDisposable handleReservation) =>
        OpenCore(path, handleReservation);

    internal static SstReader Open(string path, IDisposable? handleReservation, ResourceBudget metadataBudget) =>
        OpenCore(path, handleReservation, metadataBudget);

    static SstReader OpenCore(string path, IDisposable? handleReservation, ResourceBudget? metadataBudget = null)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            StoragePathGuard.EnsureNotLink(path);
        }
        catch
        {
            handleReservation?.Dispose();
            throw;
        }

        SafeFileHandle? file = null;
        SstMetadataAllocation? allocation = null;
        try
        {
            allocation = metadataBudget is null ? null : new SstMetadataAllocation(metadataBudget, path);
            file = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.RandomAccess);
            var fileLength = RandomAccess.GetLength(file);
            if (fileLength < DiskFormat.SstFooterSize)
            {
                if (fileLength >= sizeof(ulong))
                {
                    var trailingMagic = PositionalFile.ReadExactly(
                        file,
                        fileLength - sizeof(ulong),
                        sizeof(ulong));
                    if (BinaryPrimitives.ReadUInt64LittleEndian(trailingMagic) == DiskFormat.SstFooterMagic)
                    {
                        throw new PantsCompatibilityException(
                            "SST is shorter than its V5 footer, but matches a legacy footer magic.");
                    }
                }

                throw new PantsCorruptionException("SST is shorter than its V5 footer.");
            }

            var footerOffset = fileLength - DiskFormat.SstFooterSize;
            var footer = PositionalFile.ReadExactly(
                file,
                footerOffset,
                DiskFormat.SstFooterSize);
            SstCodec.ValidateFooter(footer);
            var metadataHandle = SstCodec.ReadHandle(footer, 0);
            var indexHandle = SstCodec.ReadHandle(footer, 16);
            var trieHandle = SstCodec.ReadOptionalHandle(footer, 32, "trie");
            var bloomHandle = SstCodec.ReadOptionalHandle(
                footer,
                48,
                "block bloom");
            var metadata = SstCodec.DecodeMetadata(
                ReadMetadataBlock(file, fileLength, metadataHandle, allocation));
            long retainedMetadataBytes = 512 + (metadata.SmallestKey?.Length ?? 0) + (metadata.LargestKey?.Length ?? 0);
            allocation?.Retain(retainedMetadataBytes);
            var index = SstCodec.DecodeIndex(
                ReadMetadataBlock(file, fileLength, indexHandle, allocation)).ToArray();
            retainedMetadataBytes = checked(retainedMetadataBytes + index.Sum(static item => item.FirstKey.Length + 56L));
            allocation?.Retain(retainedMetadataBytes);
            var blockBlooms = bloomHandle is { } bloom
                ? ReadMetadataBlock(file, fileLength, bloom, allocation)
                : null;
            if (blockBlooms is not null)
            {
                SstCodec.ValidateBlockBlooms(blockBlooms, index.Length);
                retainedMetadataBytes = checked(retainedMetadataBytes + blockBlooms.Length);
                allocation?.Retain(retainedMetadataBytes);
            }

            var trie = trieHandle is { } trieBlock
                ? ReadMetadataBlock(file, fileLength, trieBlock, allocation)
                : null;
            var trieIndex = SstCodec.DecodeTrieIndex(
                metadata.IndexKind,
                trie,
                index,
                allocation);
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
                ? SstCodec.DecodeRangeTombstones(ReadMetadataBlock(file, fileLength, rangeHandle, allocation))
                : [];
            var reader = new SstReader(
                file,
                fileLength,
                index,
                blockBlooms,
                trieIndex,
                rangeTombstones,
                handleReservation);
            allocation?.Retain(reader.EstimatedMetadataBytes);
            reader._metadataAllocation = allocation;
            allocation = null;
            file = null;
            handleReservation = null;
            return reader;
        }
        catch (PantsException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new StorageException($"Could not open SST reader for '{path}'.", exception);
        }
        finally
        {
            file?.Dispose();
            handleReservation?.Dispose();
            allocation?.Dispose();
        }
    }

    static byte[] ReadMetadataBlock(
        SafeFileHandle file, long length, SstBlockHandle handle, SstMetadataAllocation? allocation) =>
        allocation is null ? SstCodec.ReadBlock(file, length, handle) : allocation.ReadBlock(file, length, handle);

    public SstReadBlock ReadDataBlockOwned(int blockIndex, ResourceBudget? resourceBudget)
    {
        ThrowIfDisposed();
        var handle = GetDataBlockHandle(blockIndex);
        SstCodec.ValidateDataBlockSize(handle);
        return SstReadBlock.Read(_file, _fileLength, handle, resourceBudget);
    }

    public SstPointReadDecision GetPointReadDecision(ReadOnlySpan<byte> key)
    {
        ThrowIfDisposed();
        if (_index.Length == 0)
        {
            return new SstPointReadDecision(0, 0, 0, false, -1, 0);
        }

        var trieCandidate = _trieIndex?.FindFloorBlock(key) ?? -1;
        var candidate = trieCandidate >= 0
            ? trieCandidate
            : SstCodec.FindFloorBlock(_index, key);
        if (candidate < 0)
        {
            return new SstPointReadDecision(0, 0, 0, false, -1, 0);
        }

        var mightContain = _blockBlooms is null || SstCodec.BloomMightContain(_blockBlooms, candidate, key);
        var bloomChecks = _blockBlooms is null ? 0 : 1;
        var candidateHandle = _index[candidate].Handle;
        var blockSizeBytes = checked((int)candidateHandle.Size);
        return mightContain
            ? new SstPointReadDecision(bloomChecks, 1, 1, false, candidate, blockSizeBytes)
            : new SstPointReadDecision(1, 1, 0, true, candidate, blockSizeBytes);
    }

    public byte[] ReadDataBlock(int blockIndex)
    {
        ThrowIfDisposed();
        if ((uint)blockIndex >= (uint)_index.Length)
        {
            throw new PantsCorruptionException("SST point-read block index is invalid.");
        }

        SstCodec.ValidateDataBlockSize(_index[blockIndex].Handle);
        return SstCodec.ReadBlock(_file, _fileLength, _index[blockIndex].Handle);
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
