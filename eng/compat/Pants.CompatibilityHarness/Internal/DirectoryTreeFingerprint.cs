using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Pants.CompatibilityHarness.Internal;

internal static class DirectoryTreeFingerprint
{
    public static string Compute(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var fullRootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullRootPath))
        {
            throw new DirectoryNotFoundException(
                $"Cannot fingerprint missing directory '{fullRootPath}'.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entryPath in Directory
                     .EnumerateFileSystemEntries(fullRootPath, "*", SearchOption.AllDirectories)
                     .OrderBy(
                         path => NormalizeRelativePath(fullRootPath, path),
                         StringComparer.Ordinal))
        {
            var isDirectory = Directory.Exists(entryPath);
            var relativePath = NormalizeRelativePath(fullRootPath, entryPath);
            var typedRelativePath = $"{(isDirectory ? 'D' : 'F')}:{relativePath}";
            var relativePathBytes = Encoding.UTF8.GetBytes(typedRelativePath);
            AppendLength(hash, relativePathBytes.Length);
            hash.AppendData(relativePathBytes);

            var fileLength = isDirectory ? 0 : new FileInfo(entryPath).Length;
            AppendLength(hash, fileLength);
            if (isDirectory)
            {
                continue;
            }

            using var stream = File.OpenRead(entryPath);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, bytesRead);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static string NormalizeRelativePath(string rootPath, string filePath) =>
        Path.GetRelativePath(rootPath, filePath).Replace(Path.DirectorySeparatorChar, '/');

    static void AppendLength(IncrementalHash hash, long length)
    {
        var encoded = (Span<byte>)stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(encoded, length);
        hash.AppendData(encoded);
    }
}
