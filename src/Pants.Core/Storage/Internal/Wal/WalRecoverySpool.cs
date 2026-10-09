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
    /// <summary>
    ///     Spooled bytes are charged in chunks of at least this size, so a transaction of many small
    ///     records holds a handful of reservations rather than one per record.
    /// </summary>
    const long ReservationChunkBytes = 64 * 1024;

    const long FrameHeaderBytes = 2 * sizeof(uint);

    readonly StorageBudgetLedger? _ledger;
    readonly string _path;
    readonly List<StorageBudgetLedger.StorageReservation> _reservations = [];
    bool _disposed;
    long _reservedBytes;
    long _spooledBytes;
    FileStream? _stream;

    /// <summary>
    ///     <paramref name="ledger" /> charges spooled bytes against the local-storage budget, or is
    ///     <see langword="null" /> where no local budget applies. The spool sits outside the
    ///     resident figure the budget measures, so the charge is held until the spool is disposed.
    /// </summary>
    public WalRecoverySpool(string scratchDirectory, StorageBudgetLedger? ledger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);
        _path = Path.Combine(
            scratchDirectory,
            $"pants-wal-recovery-{Guid.NewGuid():N}.tmp");
        _ledger = ledger;
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
        try
        {
            DeleteSpoolFile();
        }
        finally
        {
            ReleaseReservations();
        }
    }

    void DeleteSpoolFile()
    {
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
        var payload = WalCodec.EncodeRecord(record);
        Reserve(checked(payload.Length + FrameHeaderBytes));
        var stream = OpenStream();
        WalCodec.AppendFrame(stream.SafeFileHandle, stream.Length, payload);
    }

    /// <summary>Charges the bytes about to be spooled, before writing them.</summary>
    void Reserve(long frameBytes)
    {
        _spooledBytes = checked(_spooledBytes + frameBytes);
        if (_ledger is null || _spooledBytes <= _reservedBytes)
        {
            return;
        }

        var needed = _spooledBytes - _reservedBytes;
        var chunk = Math.Max(needed, ReservationChunkBytes);
        if (!_ledger.TryReserve(StorageAdmissionKind.RecoverySpool, chunk, out var reservation))
        {
            // Near the limit the chunk's slack must not refuse bytes that would still fit.
            chunk = needed;
            if (!_ledger.TryReserve(StorageAdmissionKind.RecoverySpool, chunk, out reservation))
            {
                _spooledBytes -= frameBytes;
                throw new PantsNoSpaceException(
                    $"The local storage budget cannot admit {needed} more bytes of WAL recovery " +
                    "spool; the WAL was left untouched.");
            }
        }

        _reservations.Add(reservation);
        _reservedBytes = checked(_reservedBytes + chunk);
    }

    void ReleaseReservations()
    {
        foreach (var reservation in _reservations)
        {
            reservation.Dispose();
        }

        _reservations.Clear();
        _reservedBytes = 0;
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
