using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Pants;

internal static class AtomicStagedFile
{
    public static void Write(
        string path,
        ReadOnlySpan<byte> bytes,
        bool overwrite = true,
        Action? beforePublish = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ??
            throw new ArgumentException("A staged file path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                RandomAccess.Write(handle, bytes, 0);
                RandomAccess.FlushToDisk(handle);
            }

            beforePublish?.Invoke();
            File.Move(temporary, fullPath, overwrite);
            FlushParentDirectory(directory);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // An unpublished temporary is safer than deleting an uncertain target.
            }
            catch (UnauthorizedAccessException)
            {
                // Recovery can conservatively classify a retained staging file.
            }
        }
    }

    public static void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return;
        }

        File.Delete(fullPath);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new ArgumentException("A staged file path must have a parent directory.", nameof(path));
        FlushParentDirectory(directory);
    }

    private static void FlushParentDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows does not expose a supported directory-flush handle through
            // System.IO. The durable file handle and same-volume atomic move are
            // the strongest portable BCL boundary on that platform.
            return;
        }

        nint directoryUtf8 = Marshal.StringToCoTaskMemUTF8(directory);
        int descriptor;
        int openError;
        try
        {
            descriptor = Open(directoryUtf8, 0);
            openError = descriptor < 0 ? Marshal.GetLastPInvokeError() : 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(directoryUtf8);
        }

        if (descriptor < 0)
        {
            throw CreateUnixIOException("open", directory, openError);
        }

        try
        {
            if (Fsync(descriptor) != 0)
            {
                int fsyncError = Marshal.GetLastPInvokeError();
                throw CreateUnixIOException("fsync", directory, fsyncError);
            }
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    private static IOException CreateUnixIOException(string operation, string path, int error) =>
        new(
            $"Could not {operation} directory '{path}': {new Win32Exception(error).Message}",
            error);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(nint path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fileDescriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fileDescriptor);
}
