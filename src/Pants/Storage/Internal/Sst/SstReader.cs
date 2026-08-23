using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants.Storage.Internal.Sst;

sealed class SstReader : IDisposable
{
    readonly byte[]? _blockBlooms;
    readonly SafeFileHandle _file;
    readonly long _fileLength;
    readonly (byte[] FirstKey, SstBlockHandle Handle)[] _index;
    readonly TrieIndex? _trieIndex;
    int _disposed;

    SstReader(
        SafeFileHandle file,
        long fileLength,
        (byte[] FirstKey, SstBlockHandle Handle)[] index,
        byte[]? blockBlooms,
        TrieIndex? trieIndex)
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
            throw new StorageException("SST block index is invalid.");
        }

        return _index[blockIndex].FirstKey.ToArray();
    }

    public SstBlockHandle GetDataBlockHandle(int blockIndex)
    {
        ThrowIfDisposed();
        if ((uint)blockIndex >= (uint)_index.Length)
        {
            throw new StorageException("SST block index is invalid.");
        }

        return _index[blockIndex].Handle;
    }

    public static SstReader Open(string path)
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
                            "SST is shorter than its V4 footer, but matches a legacy footer magic.");
                    }
                }

                throw new PantsCorruptionException("SST is shorter than its V4 footer.");
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
                SstCodec.ReadBlock(file, fileLength, metadataHandle));
            var index = SstCodec.DecodeIndex(
                SstCodec.ReadBlock(file, fileLength, indexHandle)).ToArray();
            var blockBlooms = bloomHandle is { } bloom
                ? SstCodec.ReadBlock(file, fileLength, bloom)
                : null;
            if (blockBlooms is not null)
            {
                SstCodec.ValidateBlockBlooms(blockBlooms, index.Length);
            }

            var trie = trieHandle is { } trieBlock
                ? SstCodec.ReadBlock(file, fileLength, trieBlock)
                : null;
            var trieIndex = SstCodec.DecodeTrieIndex(
                metadata.IndexKind,
                trie,
                index);
            SstCodec.ValidateBlockCoverage(
                footerOffset,
                metadataHandle,
                indexHandle,
                trieHandle,
                bloomHandle,
                metadata.RangeHandle,
                index.ToList());
            var reader = new SstReader(file, fileLength, index, blockBlooms, trieIndex);
            file = null;
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
            : SstCodec.FindFloorBlock(_index, key);
        if (candidate < 0)
        {
            return new SstPointReadDecision(0, 0, 0, false, -1, 0);
        }

        var mightContain = SstCodec.BloomMightContain(_blockBlooms, candidate, key);
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
            throw new StorageException("SST point-read block index is invalid.");
        }

        return SstCodec.ReadBlock(_file, _fileLength, _index[blockIndex].Handle);
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
