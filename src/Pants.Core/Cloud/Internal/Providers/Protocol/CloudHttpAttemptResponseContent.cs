using System.Net;

namespace Cntryl.Pants.Cloud.Internal.Providers.Protocol;

sealed class CloudHttpAttemptResponseContent : HttpContent
{
    readonly Action<long> _recordBytes;
    readonly HttpContent _inner;

    public CloudHttpAttemptResponseContent(HttpContent inner, Action<long> recordBytes)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _recordBytes = recordBytes ?? throw new ArgumentNullException(nameof(recordBytes));
        foreach (var header in inner.Headers.NonValidated)
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
    {
        using var observed = new CountingStream(stream, _recordBytes, canRead: false, canWrite: true, leaveOpen: true);
        await _inner.CopyToAsync(observed, context, cancellationToken).ConfigureAwait(false);
    }

    protected override Task<Stream> CreateContentReadStreamAsync() =>
        CreateContentReadStreamAsync(CancellationToken.None);

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        var stream = await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new CountingStream(stream, _recordBytes, canRead: true, canWrite: false, leaveOpen: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    sealed class CountingStream(
        Stream inner,
        Action<long> recordBytes,
        bool canRead,
        bool canWrite,
        bool leaveOpen) : Stream
    {
        public override bool CanRead => canRead && inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => canWrite && inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Record(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Record(read);
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            Record(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Record(read);
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Record(count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Record(buffer.Length);
            inner.Write(buffer);
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            Record(count);
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Record(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        void Record(long bytes)
        {
            if (bytes > 0)
            {
                recordBytes(bytes);
            }
        }
    }
}
