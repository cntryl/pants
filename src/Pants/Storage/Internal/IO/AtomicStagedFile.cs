using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants.Storage.Internal.IO;

static class AtomicStagedFile
{
    public static void Write(
        string path,
        ReadOnlySpan<byte> bytes,
        bool overwrite = true,
        Action? beforePublish = null,
        string? temporaryFileName = null,
        Action? afterPublish = null,
        Action<string>? deleteTemporary = null,
        Action<Exception>? cleanupFailure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
                        throw new ArgumentException("A staged file path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        if (temporaryFileName is not null &&
            (!string.Equals(Path.GetFileName(temporaryFileName), temporaryFileName, StringComparison.Ordinal) ||
             string.IsNullOrWhiteSpace(temporaryFileName)))
        {
            throw new ArgumentException(
                "A staged file name must be a non-empty file name without a directory.",
                nameof(temporaryFileName));
        }

        var temporary = Path.Combine(
            directory,
            temporaryFileName ??
            $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var handle = File.OpenHandle(
                       temporary,
                       temporaryFileName is null ? FileMode.CreateNew : FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            {
                RandomAccess.Write(handle, bytes, 0);
                RandomAccess.FlushToDisk(handle);
            }

            beforePublish?.Invoke();
            File.Move(temporary, fullPath, overwrite);
            afterPublish?.Invoke();
            FlushParentDirectory(directory);
        }
        finally
        {
            try
            {
                (deleteTemporary ?? File.Delete)(temporary);
            }
            catch (IOException exception)
            {
                // An unpublished temporary is safer than deleting an uncertain target.
                ReportCleanupFailure(cleanupFailure, exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                // Recovery can conservatively classify a retained staging file.
                ReportCleanupFailure(cleanupFailure, exception);
            }
        }
    }

    static void ReportCleanupFailure(Action<Exception>? cleanupFailure, Exception exception)
    {
        try
        {
            cleanupFailure?.Invoke(exception);
        }
        catch
        {
            // Diagnostics must not replace the primary staged-write failure.
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

    public static void FlushDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        FlushParentDirectory(Path.GetFullPath(directory));
    }

    static void FlushParentDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            FlushWindowsDirectory(directory);
            return;
        }

        var directoryUtf8 = Marshal.StringToCoTaskMemUTF8(directory);
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
                var fsyncError = Marshal.GetLastPInvokeError();
                throw CreateUnixIOException("fsync", directory, fsyncError);
            }
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    static void FlushWindowsDirectory(string directory)
    {
        const uint genericRead = 0x80000000;
        const uint genericWrite = 0x40000000;
        const uint shareRead = 0x00000001;
        const uint shareWrite = 0x00000002;
        const uint shareDelete = 0x00000004;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;

        using var handle = CreateWindowsDirectoryHandle(
            directory,
            genericRead | genericWrite,
            shareRead | shareWrite | shareDelete,
            0,
            openExisting,
            backupSemantics,
            0);
        if (handle.IsInvalid)
        {
            throw CreateWindowsIOException(
                "open",
                directory,
                Marshal.GetLastPInvokeError());
        }

        if (!FlushWindowsFileBuffers(handle))
        {
            throw CreateWindowsIOException(
                "flush",
                directory,
                Marshal.GetLastPInvokeError());
        }
    }

    static IOException CreateUnixIOException(string operation, string path, int error) =>
        new(
            $"Could not {operation} directory '{path}': {new Win32Exception(error).Message}",
            error);

    static IOException CreateWindowsIOException(string operation, string path, int error) =>
        new(
            $"Could not {operation} directory '{path}': {new Win32Exception(error).Message}",
            error);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    static extern SafeFileHandle CreateWindowsDirectoryHandle(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", EntryPoint = "FlushFileBuffers", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FlushWindowsFileBuffers(SafeFileHandle fileHandle);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    static extern int Open(nint path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    static extern int Fsync(int fileDescriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    static extern int Close(int fileDescriptor);
}
