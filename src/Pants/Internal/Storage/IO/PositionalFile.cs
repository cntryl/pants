using Microsoft.Win32.SafeHandles;

namespace Pants;

internal static class PositionalFile
{
    public static byte[] ReadAllBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.RandomAccess);
        long length = RandomAccess.GetLength(handle);
        if (length > Array.MaxLength)
        {
            throw new PantsStorageException($"File '{path}' is too large to materialize.");
        }

        byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
        long offset = 0;
        while (offset < length)
        {
            int read = RandomAccess.Read(handle, bytes.AsSpan(checked((int)offset)), offset);
            if (read == 0)
            {
                throw new EndOfStreamException($"File '{path}' changed while it was being read.");
            }

            offset = checked(offset + read);
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
        using SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read,
            FileOptions.None);
        RandomAccess.Write(handle, buffers, RandomAccess.GetLength(handle));
        afterWrite?.Invoke();
        beforeFlush?.Invoke();
        RandomAccess.FlushToDisk(handle);
        afterFlush?.Invoke();
    }
}
