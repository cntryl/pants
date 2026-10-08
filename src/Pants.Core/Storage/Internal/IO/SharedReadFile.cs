namespace Cntryl.Pants.Storage.Internal.IO;

/// <summary>
///     Opens small coordination files for reading without blocking another writer from renaming
///     over or deleting them meanwhile.
/// </summary>
/// <remarks>
///     Windows refuses to replace a file while any handle that does not share delete is open on
///     it. Sharing read, write, and delete matches what Midge's readers request, so a lease reader
///     on one process never stalls a renewal or takeover on another.
/// </remarks>
static class SharedReadFile
{
    const FileShare ShareAll = FileShare.ReadWrite | FileShare.Delete;

    /// <summary>Opens <paramref name="path" />, or returns <see langword="null" /> when it is absent.</summary>
    public static FileStream? Open(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, ShareAll);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Reads <paramref name="path" />, or returns <see langword="null" /> when it is absent.</summary>
    public static byte[]? TryReadAllBytes(string path)
    {
        using var stream = Open(path);
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
