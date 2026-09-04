using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants.Storage.Internal.IO;

static class PositionalFile
{
    public delegate int ReadOperation(SafeFileHandle handle, Span<byte> buffer, long offset);

    public static byte[] ReadAllBytes(string path, ReadOperation? readOperation = null) =>
        AtomicStagedFile.WithPathLock(path, () => ReadAllBytesCore(path, readOperation));

    static byte[] ReadAllBytesCore(string path, ReadOperation? readOperation)
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
            throw new StorageException($"File '{path}' is too large to materialize.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
        long offset = 0;
        while (offset < length)
        {
            var read = (readOperation ?? RandomAccess.Read)(
                handle,
                bytes.AsSpan(checked((int)offset)),
                offset);
            if (read == 0)
            {
                throw new EndOfStreamException($"File '{path}' changed while it was being read.");
            }

            offset = checked(offset + read);
        }

        return bytes;
    }

    public static byte[] ReadExactly(
        SafeFileHandle handle,
        long offset,
        int length,
        ReadOperation? readOperation = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        var consumed = 0;
        while (consumed < bytes.Length)
        {
            var read = (readOperation ?? RandomAccess.Read)(
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

    /// <summary>
    /// Appends and durably flushes buffers. Callers must externally serialize all appends to a path.
    /// </summary>
    public static void AppendAndFlush(
        string path,
        IReadOnlyList<ReadOnlyMemory<byte>> buffers,
        Action? afterWrite = null,
        Action? beforeFlush = null,
        Action? afterFlush = null,
        Action<string>? flushDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(buffers);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
                        throw new ArgumentException("An append target must have a parent directory.", nameof(path));
        var created = !File.Exists(fullPath);
        using (var handle = File.OpenHandle(
                   fullPath,
                   FileMode.OpenOrCreate,
                   FileAccess.Write))
        {
            RandomAccess.Write(handle, buffers, RandomAccess.GetLength(handle));
            afterWrite?.Invoke();
            beforeFlush?.Invoke();
            RandomAccess.FlushToDisk(handle);
            afterFlush?.Invoke();
        }

        if (created)
        {
            (flushDirectory ?? AtomicStagedFile.FlushDirectory)(directory);
        }
    }
}
