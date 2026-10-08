namespace Cntryl.Pants.Cloud.Internal;

/// <summary>
///     A seekable read-only view of a remote object that fetches one bounded page at a time, so
///     reading it needs memory for a page rather than for the object.
/// </summary>
sealed class RemoteRangeStream(
    ICloudObjectStore store,
    string objectKey,
    long length,
    int pageBytes) : Stream
{
    byte[]? _page;
    long _pageStart;
    long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= length || buffer.IsEmpty)
        {
            return 0;
        }

        EnsurePageContainsPosition();
        var pageOffset = checked((int)(_position - _pageStart));
        var available = _page!.Length - pageOffset;
        var count = Math.Min(buffer.Length, available);
        _page.AsSpan(pageOffset, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        Position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    void EnsurePageContainsPosition()
    {
        if (_page is not null &&
            _position >= _pageStart &&
            _position < _pageStart + _page.Length)
        {
            return;
        }

        var start = _position - (_position % pageBytes);
        var size = (int)Math.Min(pageBytes, length - start);
        var range = store.GetRangeAsync(objectKey, checked((ulong)start), size, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        if (range is null || range.Data.Length != size)
        {
            throw new PantsCorruptionException(
                $"Published cloud WAL object '{objectKey}' did not return the requested range.");
        }

        _page = range.Data.ToArray();
        _pageStart = start;
    }
}
