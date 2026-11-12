using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants.Storage.Internal.IO;

static class AtomicStagedFile
{
    const int PublishLockCount = 64;
    static readonly object[] PublishLocks = CreatePublishLocks();

    /// <summary>
    ///     Creates a directory path and flushes each parent directory after establishing a child
    ///     directory entry. Existing paths also have their parent flushed so a retry after an
    ///     interrupted initialization re-establishes the same durability boundary.
    /// </summary>
    public static void CreateDirectoryDurably(
        string path,
        Action? beforeParentDirectorySync = null,
        MetadataIoObservation? observation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var missing = new Stack<string>();
        var current = fullPath;
        while (!Directory.Exists(current))
        {
            missing.Push(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) ||
                StringComparerForPaths.Equals(parent, current))
            {
                throw new DirectoryNotFoundException(
                    $"The parent directory for '{fullPath}' could not be resolved.");
            }

            current = parent;
        }

        var created = false;
        while (missing.TryPop(out var child))
        {
            MetadataIoObservation.Run(observation, () => Directory.CreateDirectory(child));
            SyncParentDirectory(child, beforeParentDirectorySync, observation);
            created = true;
        }

        if (!created)
        {
            SyncParentDirectory(fullPath, beforeParentDirectorySync, observation);
        }
    }

    /// <summary>
    ///     Stages a file whose payload is produced incrementally, so the caller never holds the
    ///     whole contents in memory.
    /// </summary>
    /// <remarks>
    ///     The callback is asynchronous because producing the payload generally means fetching it,
    ///     and a synchronous callback would force the caller to buffer everything first, which is
    ///     exactly what this exists to avoid.
    /// </remarks>
    public static async ValueTask WriteStreamedAsync(
        string path,
        Func<SafeFileHandle, ValueTask> writePayloadAsync,
        bool overwrite = true,
        MetadataIoObservation? observation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(writePayloadAsync);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
                        throw new ArgumentException(
                            "A staged file path must have a parent directory.",
                            nameof(path));
        StoragePathGuard.EnsureNotLink(fullPath);
        MetadataIoObservation.Run(observation, () => Directory.CreateDirectory(directory));
        StoragePathGuard.EnsureNotLink(fullPath);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var handle = MetadataIoObservation.Run(observation, () => File.OpenHandle(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None)))
            {
                observation?.Mutation();
                try
                {
                    await writePayloadAsync(handle).ConfigureAwait(false);
                }
                finally
                {
                    observation?.Mutation();
                }
                MetadataIoObservation.Run(observation, () => RandomAccess.FlushToDisk(handle));
            }

            lock (GetPublishLock(fullPath))
            {
                MetadataIoObservation.Run(observation, () => TransientSharingRetry.Move(temporary, fullPath, overwrite));
                MetadataIoObservation.Run(observation, () => FlushParentDirectory(directory));
            }
        }
        finally
        {
            try
            {
                MetadataIoObservation.Run(observation, () => TransientSharingRetry.Delete(temporary));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // An unpublished temporary is safer than deleting an uncertain target.
            }
        }
    }

    public static void Write(
        string path,
        ReadOnlySpan<byte> bytes,
        bool overwrite = true,
        Action? beforePublish = null,
        string? temporaryFileName = null,
        Action? afterPublish = null,
        Action<string>? deleteTemporary = null,
        Action<Exception>? cleanupFailure = null,
        MetadataIoObservation? observation = null,
        Action<string>? flushDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
                        throw new ArgumentException("A staged file path must have a parent directory.", nameof(path));
        StoragePathGuard.EnsureNotLink(fullPath);
        MetadataIoObservation.Run(observation, () => Directory.CreateDirectory(directory));
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
            using (var handle = MetadataIoObservation.Run(observation, () => File.OpenHandle(
                       temporary,
                       temporaryFileName is null ? FileMode.CreateNew : FileMode.Create,
                       FileAccess.Write,
                       FileShare.None)))
            {
                observation?.Issued((ulong)bytes.Length);
                try
                {
                    RandomAccess.Write(handle, bytes, 0);
                    observation?.Returned((ulong)bytes.Length);
                }
                catch
                {
                    observation?.Mutation();
                    throw;
                }

                MetadataIoObservation.Run(observation, () => RandomAccess.FlushToDisk(handle));
            }

            beforePublish?.Invoke();
            lock (GetPublishLock(fullPath))
            {
                MetadataIoObservation.Run(observation, () => TransientSharingRetry.Move(temporary, fullPath, overwrite));
                afterPublish?.Invoke();
                MetadataIoObservation.Run(observation, () => (flushDirectory ?? FlushParentDirectory)(directory));
            }
        }
        finally
        {
            try
            {
                MetadataIoObservation.Run(observation, () => (deleteTemporary ?? TransientSharingRetry.Delete)(temporary));
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

    static object GetPublishLock(string path)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var hash = comparer.GetHashCode(path) & int.MaxValue;
        return PublishLocks[hash % PublishLocks.Length];
    }

    public static T WithPathLock<T>(string path, Func<T> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(operation);
        lock (GetPublishLock(Path.GetFullPath(path)))
        {
            return operation();
        }
    }

    static object[] CreatePublishLocks()
    {
        var locks = new object[PublishLockCount];
        for (var index = 0; index < locks.Length; index++)
        {
            locks[index] = new object();
        }

        return locks;
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

    public static void Delete(string path) => Delete(path, null);

    public static void Delete(string path, MetadataIoObservation? observation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        StoragePathGuard.EnsureNotLink(fullPath);
        if (!File.Exists(fullPath))
        {
            return;
        }

        MetadataIoObservation.Run(observation, () => File.Delete(fullPath));
        var directory = Path.GetDirectoryName(fullPath) ??
                        throw new ArgumentException("A staged file path must have a parent directory.", nameof(path));
        MetadataIoObservation.Run(observation, () => FlushParentDirectory(directory));
    }

    public static void FlushDirectory(string directory) => FlushDirectory(directory, null);

    public static void FlushDirectory(string directory, MetadataIoObservation? observation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        MetadataIoObservation.Run(observation, () => FlushParentDirectory(Path.GetFullPath(directory)));
    }

    static void SyncParentDirectory(string path, Action? beforeParentDirectorySync, MetadataIoObservation? observation)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is null)
        {
            return;
        }

        beforeParentDirectorySync?.Invoke();
        MetadataIoObservation.Run(observation, () => FlushParentDirectory(parent));
    }

    static StringComparer StringComparerForPaths =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

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
