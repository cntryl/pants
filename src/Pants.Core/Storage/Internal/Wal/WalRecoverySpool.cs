using System.Buffers.Binary;

namespace Cntryl.Pants.Storage.Internal.Wal;

/// <summary>
///     Holds the operations of one transaction that was still open in the WAL, until its commit
///     record says whether to apply them.
/// </summary>
/// <remarks>
///     Recovery creates one of these per open transaction, and nothing bounds how many a WAL can
///     contain. The backing file is therefore deferred until a record is actually spooled, so a WAL
///     carrying many empty or interleaved transactions costs no file handles. Bounding the count
///     instead would risk refusing a WAL this engine legitimately wrote.
/// </remarks>
sealed class WalRecoverySpool : IDisposable
{
    readonly string _path;
    bool _disposed;
    FileStream? _stream;

    public WalRecoverySpool(string scratchDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);
        _path = Path.Combine(
            scratchDirectory,
            $"pants-wal-recovery-{Guid.NewGuid():N}.tmp");
    }

    /// <summary>
    ///     Removes spools left behind by a process that died mid-recovery. <c>DeleteOnClose</c>
    ///     covers the normal path; this covers the abnormal one.
    /// </summary>
    public static void CleanupOrphans(string databasePath)
    {
        var directory = Path.Combine(databasePath, "recovery");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_stream is null)
        {
            return;
        }

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

    public void Append(WalRecord record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stream = OpenStream();
        var payload = WalCodec.EncodeRecord(record);
        WalCodec.AppendFrame(stream.SafeFileHandle, stream.Length, payload);
    }

    public void Replay(Action<WalRecord> accept)
    {
        ArgumentNullException.ThrowIfNull(accept);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is not { } stream)
        {
            return;
        }

        stream.Flush(false);
        stream.Position = 0;
        Span<byte> header = stackalloc byte[2 * sizeof(uint)];
        while (stream.Position < stream.Length)
        {
            if (!DiskFormat.ReadExactly(stream, header))
            {
                throw new StorageException("A recovery transaction spool has a torn frame header.");
            }

            var encodedLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (encodedLength > DiskFormat.WalMaximumRecordBytes)
            {
                throw new StorageException("A recovery transaction spool frame is oversized.");
            }

            var payload = GC.AllocateUninitializedArray<byte>(checked((int)encodedLength));
            if (!DiskFormat.ReadExactly(stream, payload))
            {
                throw new StorageException("A recovery transaction spool has a torn frame payload.");
            }

            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
                header[sizeof(uint)..]);
            if (DiskFormat.Crc32C(payload) != expectedCrc)
            {
                throw new StorageException("A recovery transaction spool frame is corrupt.");
            }

            accept(WalCodec.DecodeRecord(payload));
        }
    }

    FileStream OpenStream()
    {
        if (_stream is { } existing)
        {
            return existing;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _stream = new FileStream(
            _path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            4_096,
            FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        return _stream;
    }
}
