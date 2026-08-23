using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants.Storage.Internal.IO;

static class PositionalFile
{
    public static byte[] ReadAllBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.RandomAccess);
        var length = RandomAccess.GetLength(handle);
        if (length > Array.MaxLength)
        {
            throw new PantsStorageException($"File '{path}' is too large to materialize.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
        long offset = 0;
        while (offset < length)
        {
            var read = RandomAccess.Read(handle, bytes.AsSpan(checked((int)offset)), offset);
            if (read == 0)
            {
                throw new EndOfStreamException($"File '{path}' changed while it was being read.");
            }

            offset = checked(offset + read);
        }

        return bytes;
    }

    public static byte[] ReadExactly(SafeFileHandle handle, long offset, int length)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        var consumed = 0;
        while (consumed < bytes.Length)
        {
            var read = RandomAccess.Read(
                handle,
                bytes.AsSpan(consumed),
                checked(offset + consumed));
            if (read == 0)
            {
                throw new EndOfStreamException("The file changed during a positional read.");
            }

            consumed = checked(consumed + read);
        }

        return bytes;
    }

    public static void AppendAndFlush(
        string path,
        IReadOnlyList<ReadOnlyMemory<byte>> buffers,
        Action? afterWrite = null,
        Action? beforeFlush = null,
        Action? afterFlush = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(buffers);
        using var handle = File.OpenHandle(
            path,
            FileMode.OpenOrCreate,
            FileAccess.Write);
        RandomAccess.Write(handle, buffers, RandomAccess.GetLength(handle));
        afterWrite?.Invoke();
        beforeFlush?.Invoke();
        RandomAccess.FlushToDisk(handle);
        afterFlush?.Invoke();
    }
}
