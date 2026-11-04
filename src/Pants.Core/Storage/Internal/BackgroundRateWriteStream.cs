namespace Cntryl.Pants.Storage.Internal;

/// <summary>Charges bounded physical output before delegating each encoded chunk.</summary>
sealed class BackgroundRateWriteStream(
    Stream destination,
    BackgroundWriteLimiter limiter,
    CancellationToken cancellationToken = default) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => destination.CanSeek;
    public override bool CanWrite => destination.CanWrite;
    public override long Length => destination.Length;
    public override long Position { get => destination.Position; set => destination.Position = value; }
    public override void Flush() => destination.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => destination.Seek(offset, origin);
    public override void SetLength(long value) => destination.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var count = Math.Min(buffer.Length, 64 * 1024);
            limiter.Acquire(count, cancellationToken);
            destination.Write(buffer[..count]);
            buffer = buffer[count..];
        }
    }
    public override void WriteByte(byte value)
    {
        limiter.Acquire(1, cancellationToken);
        destination.WriteByte(value);
    }
}
