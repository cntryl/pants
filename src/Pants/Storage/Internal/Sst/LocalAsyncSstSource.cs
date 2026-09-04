using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants.Storage.Internal.Sst;

sealed class LocalAsyncSstSource : IAsyncSstSource
{
    readonly SafeFileHandle _handle;
    int _disposed;

    LocalAsyncSstSource(SafeFileHandle handle, long length)
    {
        _handle = handle;
        Length = length;
    }

    public long Length { get; }

    public static LocalAsyncSstSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            return new LocalAsyncSstSource(handle, RandomAccess.GetLength(handle));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public async ValueTask<byte[]> ReadExactlyAsync(
        long offset,
        int length,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset > Length || length > Length - offset)
        {
            throw new EndOfStreamException("The SST range is outside the file.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(length);
        var consumed = 0;
        while (consumed < length)
        {
            var read = await RandomAccess.ReadAsync(
                    _handle,
                    bytes.AsMemory(consumed),
                    checked(offset + consumed),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The SST changed during a positional read.");
            }

            consumed = checked(consumed + read);
        }

        return bytes;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _handle.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
