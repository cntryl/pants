using System.Buffers.Binary;

namespace Cntryl.Pants.Storage.Internal.Wal;

sealed class MidgeWalRecoverySpool : IDisposable
{
    readonly string _path;
    readonly FileStream _stream;
    bool _disposed;

    public MidgeWalRecoverySpool()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pants-wal-recovery-{Guid.NewGuid():N}.tmp");
        _stream = new FileStream(
            _path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            4_096,
            FileOptions.DeleteOnClose | FileOptions.SequentialScan);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // DeleteOnClose is the primary cleanup mechanism. A concurrent
            // scanner can briefly retain the name on Windows.
        }
        catch (UnauthorizedAccessException)
        {
            // DeleteOnClose still owns cleanup when an antivirus scanner has
            // temporarily denied an explicit delete.
        }
    }

    public void Append(MidgeWalRecord record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var payload = MidgeWalCodec.EncodeRecord(record);
        MidgeWalCodec.AppendFrame(_stream.SafeFileHandle, _stream.Length, payload);
    }

    public void Replay(Action<MidgeWalRecord> accept)
    {
        ArgumentNullException.ThrowIfNull(accept);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Flush(false);
        _stream.Position = 0;
        Span<byte> header = stackalloc byte[2 * sizeof(uint)];
        while (_stream.Position < _stream.Length)
        {
            if (!MidgeDiskFormat.ReadExactly(_stream, header))
            {
                throw new PantsStorageException("A recovery transaction spool has a torn frame header.");
            }

            var encodedLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (encodedLength > MidgeDiskFormat.WalMaximumRecordBytes)
            {
                throw new PantsStorageException("A recovery transaction spool frame is oversized.");
            }

            var payload = GC.AllocateUninitializedArray<byte>(checked((int)encodedLength));
            if (!MidgeDiskFormat.ReadExactly(_stream, payload))
            {
                throw new PantsStorageException("A recovery transaction spool has a torn frame payload.");
            }

            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
                header[sizeof(uint)..]);
            if (MidgeDiskFormat.Crc32C(payload) != expectedCrc)
            {
                throw new PantsStorageException("A recovery transaction spool frame is corrupt.");
            }

            accept(MidgeWalCodec.DecodeRecord(payload));
        }
    }
}
