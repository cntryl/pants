namespace Cntryl.Pants.Storage.Internal.Sst;

sealed class Crc32CWriteStream(Stream destination) : Stream
{
    public uint Checksum { get; private set; }

    public long BytesWritten { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => destination.CanSeek;

    public override bool CanWrite => true;

    public override long Length => destination.Length;

    public override long Position
    {
        get => destination.Position;
        set => destination.Position = value;
    }

    public override void Flush() => destination.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => destination.Seek(offset, origin);

    public override void SetLength(long value) => destination.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        destination.Write(buffer);
        Checksum = DiskFormat.Crc32CAppend(Checksum, buffer);
        BytesWritten = checked(BytesWritten + buffer.Length);
    }
}
