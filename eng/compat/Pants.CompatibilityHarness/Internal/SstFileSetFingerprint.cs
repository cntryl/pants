using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Pants.CompatibilityHarness.Internal;

internal static class SstFileSetFingerprint
{
    public static string Compute(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullDatabasePath = Path.GetFullPath(databasePath);
        var files = Directory
            .EnumerateFiles(fullDatabasePath, "*.sst", SearchOption.AllDirectories)
            .OrderBy(
                path => Path.GetRelativePath(fullDatabasePath, path),
                StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException(
                $"Cannot fingerprint an empty SST set under '{fullDatabasePath}'.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var filePath in files)
        {
            var relativePath = Path.GetRelativePath(fullDatabasePath, filePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            var pathBytes = Encoding.UTF8.GetBytes(relativePath);
            AppendLength(hash, pathBytes.Length);
            hash.AppendData(pathBytes);

            using var stream = File.OpenRead(filePath);
            AppendLength(hash, stream.Length);
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

    static void AppendLength(IncrementalHash hash, long length)
    {
        var encoded = (Span<byte>)stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(encoded, length);
        hash.AppendData(encoded);
    }
}
