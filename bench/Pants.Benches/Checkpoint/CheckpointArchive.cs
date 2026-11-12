using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Cntryl.Pants.Checkpoint;

static class CheckpointArchive
{
    public static CheckpointEvidence.CapturedAttempt Capture(string directory, string archivePath,
        string key, CheckpointEvidence.ProcessOutcome outcome)
    {
        ZipFile.CreateFromDirectory(directory, archivePath, CompressionLevel.Fastest, false);
        var hashes = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
            hashes.Add(name, CheckpointProvenance.HashFile(path));
        }
        return new CheckpointEvidence.CapturedAttempt(key, outcome, Path.GetFileName(archivePath),
            CheckpointProvenance.HashFile(archivePath), hashes.ToImmutable());
    }

    public static void Verify(string root, CheckpointEvidence.CapturedAttempt attempt)
    {
        var archivePath = Resolve(root, attempt.Archive);
        if (CheckpointProvenance.HashFile(archivePath) != attempt.ArchiveSha256)
        {
            throw new InvalidOperationException("Captured attempt archive digest differs.");
        }
        using var archive = ZipFile.OpenRead(archivePath);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            _ = Resolve(root, entry.FullName);
            if (!observed.Add(entry.FullName) || entry.Length > 16 * 1024 * 1024 ||
                !attempt.ContentsSha256.TryGetValue(entry.FullName, out var expected))
            {
                throw new InvalidOperationException("Captured archive members differ or exceed their allowance.");
            }
            using var input = entry.Open();
            var hash = Convert.ToHexString(SHA256.HashData(input));
            var extracted = Resolve(Path.Combine(root, "attempts", attempt.Key), entry.FullName);
            if (hash != expected || CheckpointProvenance.HashFile(extracted) != hash)
            {
                throw new InvalidOperationException("Extracted attempt differs from its captured archive.");
            }
        }
        if (observed.Count != attempt.ContentsSha256.Count)
        {
            throw new InvalidOperationException("Captured archive is incomplete.");
        }
    }

    public static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') ||
            relative.Split('/').Any(static component => component is "" or "." or ".."))
        {
            throw new InvalidOperationException("Evidence member path is not a safe relative path.");
        }
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        var current = path;
        while (current.Length >= root.Length)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Linked evidence is not accepted.");
            }
            current = Path.GetDirectoryName(current) ?? "";
        }
        return path;
    }
}
