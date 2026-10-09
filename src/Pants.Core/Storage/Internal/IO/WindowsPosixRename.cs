using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants.Storage.Internal.IO;

/// <summary>
///     Renames a file over an existing destination with POSIX semantics on Windows
///     (<c>FileRenameInfoEx</c> with <c>FILE_RENAME_FLAG_POSIX_SEMANTICS</c>), the same fallback
///     Midge and Rust's standard library use.
/// </summary>
static class WindowsPosixRename
{
    const uint DeleteAccess = 0x00010000;
    const uint SynchronizeAccess = 0x00100000;
    const uint ShareReadWriteDelete = 0x00000007;
    const uint OpenExisting = 3;
    const uint OpenReparsePoint = 0x00200000;
    const int FileRenameInfoEx = 22;
    const uint ReplaceIfExists = 0x00000001;
    const uint PosixSemantics = 0x00000002;

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="source" /> now has the name
    ///     <paramref name="destination" />, and <see langword="false" /> when the rename was not
    ///     performed (not Windows, an older Windows or file system, or any other refusal).
    /// </summary>
    public static bool TryReplace(string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var handle = CreateFile(
            Path.GetFullPath(source),
            DeleteAccess | SynchronizeAccess,
            ShareReadWriteDelete,
            0,
            OpenExisting,
            OpenReparsePoint,
            0);
        if (handle.IsInvalid)
        {
            return false;
        }

        // FILE_RENAME_INFO: { union { BOOLEAN ReplaceIfExists; DWORD Flags; }; HANDLE RootDirectory;
        // DWORD FileNameLength; WCHAR FileName[]; } with natural alignment.
        var name = Path.GetFullPath(destination);
        var rootDirectoryOffset = IntPtr.Size;
        var nameLengthOffset = 2 * IntPtr.Size;
        var nameOffset = nameLengthOffset + sizeof(uint);
        var nameBytes = name.Length * sizeof(char);
        var size = Math.Max(nameOffset + nameBytes + sizeof(char), 3 * IntPtr.Size);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, 0, unchecked((int)(ReplaceIfExists | PosixSemantics)));
            Marshal.WriteIntPtr(buffer, rootDirectoryOffset, 0);
            Marshal.WriteInt32(buffer, nameLengthOffset, nameBytes);
            Marshal.Copy(name.ToCharArray(), 0, buffer + nameOffset, name.Length);
            return SetFileInformationByHandle(handle, FileRenameInfoEx, buffer, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        nint fileInformation,
        uint bufferSize);
}
