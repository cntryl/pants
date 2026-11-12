using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants.Storage.Internal.IO;

/// <summary>Owns cache exclusion before remote startup and transfers the same handle to storage.</summary>
sealed class DatabasePathLock : IDisposable
{
    readonly string _root;
    readonly FileIdentity _identity;
    FileStream? _stream;

    DatabasePathLock(string root, FileStream stream)
    {
        _root = root;
        _stream = stream;
        _identity = ReadIdentity(stream.SafeFileHandle);
    }

    public static DatabasePathLock Acquire(string directory)
    {
        var root = StoragePathGuard.CanonicalizeRoot(directory);
        StoragePathGuard.EnsureTreeHasNoLinks(root);
        AtomicStagedFile.CreateDirectoryDurably(root);
        StoragePathGuard.EnsureTreeHasNoLinks(root);
        var path = Path.Combine(root, "LOCK");
        StoragePathGuard.EnsureNotLink(path);
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                var result = new DatabasePathLock(root, stream);
                result.EnsureValid();
                return result;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (IOException exception)
        {
            throw new PantsLeaseHeldException("Another Midge-compatible writer holds the database lock.", exception);
        }
    }

    public bool OwnsCoordinationFile(string root, string path)
    {
        EnsureValid(root);
        return StringComparer.Ordinal.Equals(Path.GetRelativePath(root, path), "LOCK");
    }

    public void EnsureValid(string? root = null)
    {
        if (_stream is null)
        {
            throw new PantsInternalException("The startup database exclusion was already transferred or released.");
        }
        var path = Path.Combine(StoragePathGuard.CanonicalizeRoot(root ?? _root), "LOCK");
        StoragePathGuard.EnsureNotLink(path);
        if (_identity != ReadIdentity(path))
        {
            throw new PantsInvalidPathException("The database cache directory or its coordination file changed during startup.");
        }
    }

    public FileStream TransferToStore(string root)
    {
        EnsureValid(root);
        return Interlocked.Exchange(ref _stream, null) ??
               throw new PantsInternalException("The startup database exclusion was already transferred or released.");
    }

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();

    static FileIdentity ReadIdentity(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Zero requested access queries identity without conflicting with our own exclusive
            // data handle. This handle never admits a second writer.
            using var handle = OpenWindowsIdentity(path, 0, 7, 0, 3, 0x00200000, 0);
            if (handle.IsInvalid)
            {
                throw new PantsInvalidPathException("The startup coordination file is no longer accessible.");
            }
            return ReadIdentity(handle);
        }
        var nativePath = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            var result = OperatingSystem.IsMacOS() ? StatMac(nativePath, out var information) : StatLinux(nativePath, out information);
            return ReadUnixIdentity(result, information);
        }
        finally
        {
            Marshal.FreeCoTaskMem(nativePath);
        }
    }

    static FileIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetWindowsIdentity(handle, 18, out var identity, 24))
            {
                throw new PantsInvalidPathException("The startup coordination file identity could not be read.");
            }
            return new FileIdentity(identity.Volume, identity.Low, identity.High);
        }
        var descriptor = handle.DangerousGetHandle().ToInt32();
        var result = OperatingSystem.IsMacOS() ? FStatMac(descriptor, out var information) : FStatLinux(descriptor, out information);
        return ReadUnixIdentity(result, information);
    }

    static FileIdentity ReadUnixIdentity(int result, UnixFileInformation information)
    {
        if (result != 0)
        {
            throw new PantsInvalidPathException("The startup coordination file identity could not be read.");
        }
        // Darwin's dev_t is 32 bits followed by mode and link count; Linux's dev_t is 64 bits.
        var device = OperatingSystem.IsMacOS() ? unchecked((uint)information.Device) : information.Device;
        return new FileIdentity(device, information.Inode, 0);
    }

    readonly record struct FileIdentity(ulong Device, ulong Low, ulong High);

    // Both supported Unix 64-bit stat ABIs place the inode at offset eight. Reserve the complete
    // native output area rather than allowing the native call to write beyond a partial struct.
    [StructLayout(LayoutKind.Explicit, Size = 512)]
    struct UnixFileInformation
    {
        [FieldOffset(0)] public ulong Device;
        [FieldOffset(8)] public ulong Inode;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowsFileIdentity
    {
        public ulong Volume;
        public ulong Low;
        public ulong High;
    }

    [DllImport("libc", EntryPoint = "stat64", SetLastError = true)]
    static extern int StatMac(nint path, out UnixFileInformation information);

    [DllImport("libc", EntryPoint = "fstat64", SetLastError = true)]
    static extern int FStatMac(int descriptor, out UnixFileInformation information);

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    static extern int StatLinux(nint path, out UnixFileInformation information);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    static extern int FStatLinux(int descriptor, out UnixFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle OpenWindowsIdentity(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetWindowsIdentity(SafeFileHandle handle, int informationClass,
        out WindowsFileIdentity information, uint size);
}
