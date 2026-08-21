using Microsoft.Win32.SafeHandles;

namespace Pants;

internal sealed class MidgeSstReader : IDisposable
{
    private readonly SafeFileHandle _file;
    private readonly long _fileLength;
    private readonly (byte[] FirstKey, MidgeSstBlockHandle Handle)[] _index;
    private readonly byte[]? _blockBlooms;
    private readonly MidgeTrieIndex? _trieIndex;
    private int _disposed;

    private MidgeSstReader(
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
            long fileLength = RandomAccess.GetLength(file);
            if (fileLength < MidgeDiskFormat.SstFooterSize)
            {
                throw new PantsStorageException("SST is shorter than its V4 footer.");
            }

            long footerOffset = fileLength - MidgeDiskFormat.SstFooterSize;
            byte[] footer = PositionalFile.ReadExactly(
                file,
                footerOffset,
                MidgeDiskFormat.SstFooterSize);
            MidgeSstCodec.ValidateFooter(footer);
            MidgeSstBlockHandle metadataHandle = MidgeSstCodec.ReadHandle(footer, 0);
            MidgeSstBlockHandle indexHandle = MidgeSstCodec.ReadHandle(footer, 16);
            MidgeSstBlockHandle? trieHandle = MidgeSstCodec.ReadOptionalHandle(footer, 32, "trie");
            MidgeSstBlockHandle? bloomHandle = MidgeSstCodec.ReadOptionalHandle(
                footer,
                48,
                "block bloom");
            MidgeSstMetadata metadata = MidgeSstCodec.DecodeMetadata(
                MidgeSstCodec.ReadBlock(file, fileLength, metadataHandle));
            (byte[] FirstKey, MidgeSstBlockHandle Handle)[] index = MidgeSstCodec.DecodeIndex(
                MidgeSstCodec.ReadBlock(file, fileLength, indexHandle)).ToArray();
            byte[]? blockBlooms = bloomHandle is { } bloom
                ? MidgeSstCodec.ReadBlock(file, fileLength, bloom)
                : null;
            if (blockBlooms is not null)
            {
                MidgeSstCodec.ValidateBlockBlooms(blockBlooms, index.Length);
            }

            byte[]? trie = trieHandle is { } trieBlock
                ? MidgeSstCodec.ReadBlock(file, fileLength, trieBlock)
                : null;
            MidgeTrieIndex? trieIndex = MidgeSstCodec.DecodeTrieIndex(
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

        int trieCandidate = _trieIndex?.FindFloorBlock(key) ?? -1;
        int candidate = trieCandidate >= 0
            ? trieCandidate
            : MidgeSstCodec.FindFloorBlock(_index, key);
        if (candidate < 0)
        {
            return new SstPointReadDecision(0, 0, 0, false, -1, 0);
        }

        bool mightContain = MidgeSstCodec.BloomMightContain(_blockBlooms, candidate, key);
        MidgeSstBlockHandle candidateHandle = _index[candidate].Handle;
        int blockSizeBytes = checked((int)candidateHandle.Size);
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _file.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
