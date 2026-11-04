namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

/// <summary>Checks the remaining scratch allowance before any encoded bytes land.</summary>
sealed class CompactionScratchWriteStream(string path, long capacityBytes) : Stream
{
    readonly FileStream _destination = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    public override bool CanRead => false;

    public override bool CanSeek => true;

    public override bool CanWrite => true;

    public override long Length => _destination.Length;

    public override long Position
    {
        get => _destination.Position;
        set => _destination.Position = value;
    }

    public override void Flush() => _destination.Flush();

    public void Flush(bool flushToDisk) => _destination.Flush(flushToDisk);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => _destination.Seek(offset, origin);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Admit(buffer.Length);
        _destination.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        Admit(1);
        _destination.WriteByte(value);
    }

    public override void SetLength(long value)
    {
        if (value > capacityBytes)
        {
            throw CapacityExceeded();
        }

        _destination.SetLength(value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _destination.Dispose();
        }

        base.Dispose(disposing);
    }

    void Admit(int bytes)
    {
        if (bytes > capacityBytes - Position)
        {
            throw CapacityExceeded();
        }
    }

    static PantsResourceLimitException CapacityExceeded() =>
        new("Overlap repair scratch exceeds its admitted local capacity.");
}
