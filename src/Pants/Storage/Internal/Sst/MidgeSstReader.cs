using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants.Storage.Internal.Sst;

sealed class MidgeSstReader : IDisposable
{
    readonly byte[]? _blockBlooms;
    readonly SafeFileHandle _file;
    readonly long _fileLength;
    readonly (byte[] FirstKey, MidgeSstBlockHandle Handle)[] _index;
    readonly MidgeTrieIndex? _trieIndex;
    int _disposed;

    MidgeSstReader(
        SafeFileHandle file,
        long fileLength,
        (byte[] FirstKey, MidgeSstBlockHandle Handle)[] index,
        byte[]? blockBlooms,
        MidgeTrieIndex? trieIndex)
    {
        _file = file;
        _fileLength = fileLength;
        _index = index;
        _blockBlooms = blockBlooms;
        _trieIndex = trieIndex;
    }

    public int DataBlockCount => _index.Length;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _file.Dispose();
        }
    }

    public byte[] GetFirstKey(int blockIndex)
    {
        ThrowIfDisposed();
        if ((uint)blockIndex >= (uint)_index.Length)
        {
            throw new PantsStorageException("SST block index is invalid.");
        }

        return _index[blockIndex].FirstKey.ToArray();
    }

    public MidgeSstBlockHandle GetDataBlockHandle(int blockIndex)
    {
        ThrowIfDisposed();
        if ((uint)blockIndex >= (uint)_index.Length)
        {
            throw new PantsStorageException("SST block index is invalid.");
        }

        return _index[blockIndex].Handle;
    }

    public static MidgeSstReader Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        SafeFileHandle? file = null;
        try
        {
            file = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.RandomAccess);
            var fileLength = RandomAccess.GetLength(file);
            if (fileLength < MidgeDiskFormat.SstFooterSize)
            {
                throw new PantsStorageException("SST is shorter than its V4 footer.");
            }

            var footerOffset = fileLength - MidgeDiskFormat.SstFooterSize;
            var footer = PositionalFile.ReadExactly(
                file,
                footerOffset,
                MidgeDiskFormat.SstFooterSize);
            MidgeSstCodec.ValidateFooter(footer);
            var metadataHandle = MidgeSstCodec.ReadHandle(footer, 0);
            var indexHandle = MidgeSstCodec.ReadHandle(footer, 16);
            var trieHandle = MidgeSstCodec.ReadOptionalHandle(footer, 32, "trie");
            var bloomHandle = MidgeSstCodec.ReadOptionalHandle(
                footer,
                48,
                "block bloom");
            var metadata = MidgeSstCodec.DecodeMetadata(
                MidgeSstCodec.ReadBlock(file, fileLength, metadataHandle));
            var index = MidgeSstCodec.DecodeIndex(
                MidgeSstCodec.ReadBlock(file, fileLength, indexHandle)).ToArray();
            var blockBlooms = bloomHandle is { } bloom
                ? MidgeSstCodec.ReadBlock(file, fileLength, bloom)
                : null;
            if (blockBlooms is not null)
            {
                MidgeSstCodec.ValidateBlockBlooms(blockBlooms, index.Length);
            }

            var trie = trieHandle is { } trieBlock
                ? MidgeSstCodec.ReadBlock(file, fileLength, trieBlock)
                : null;
            var trieIndex = MidgeSstCodec.DecodeTrieIndex(
                metadata.IndexKind,
                trie,
                index);
            MidgeSstCodec.ValidateBlockCoverage(
                footerOffset,
                metadataHandle,
                indexHandle,
                trieHandle,
                bloomHandle,
                metadata.RangeHandle,
                index.ToList());
            var reader = new MidgeSstReader(file, fileLength, index, blockBlooms, trieIndex);
            file = null;
            return reader;
        }
        catch (PantsException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new PantsStorageException($"Could not open SST reader for '{path}'.", exception);
        }
        finally
        {
            file?.Dispose();
        }
    }

    public SstPointReadDecision GetPointReadDecision(ReadOnlySpan<byte> key)
    {
        ThrowIfDisposed();
        if (_index.Length == 0 || _blockBlooms is null)
        {
            return new SstPointReadDecision(0, 0, 0, false, -1, 0);
        }

        var trieCandidate = _trieIndex?.FindFloorBlock(key) ?? -1;
        var candidate = trieCandidate >= 0
            ? trieCandidate
            : MidgeSstCodec.FindFloorBlock(_index, key);
        if (candidate < 0)
        {
            return new SstPointReadDecision(0, 0, 0, false, -1, 0);
        }

        var mightContain = MidgeSstCodec.BloomMightContain(_blockBlooms, candidate, key);
        var candidateHandle = _index[candidate].Handle;
        var blockSizeBytes = checked((int)candidateHandle.Size);
        return mightContain
            ? new SstPointReadDecision(1, 1, 1, false, candidate, blockSizeBytes)
            : new SstPointReadDecision(1, 1, 0, true, candidate, blockSizeBytes);
    }

    public byte[] ReadDataBlock(int blockIndex)
    {
        ThrowIfDisposed();
        if ((uint)blockIndex >= (uint)_index.Length)
        {
            throw new PantsStorageException("SST point-read block index is invalid.");
        }

        return MidgeSstCodec.ReadBlock(_file, _fileLength, _index[blockIndex].Handle);
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
