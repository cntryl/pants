using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace Cntryl.Pants.Storage.Internal.IO;

/// <summary>
///     Refuses symbolic links and reparse points (including Windows junctions) inside a store so a
///     planted link cannot redirect SST, WAL, manifest, or lease IO outside it.
/// </summary>
/// <remarks>
///     The store root itself may be a link and is never checked. There is an unavoidable window
///     between a check and the IO that follows it; a link planted inside that window is not
///     detected, which matches the reference engine.
/// </remarks>
static class StoragePathGuard
{
    static readonly ConcurrentDictionary<string, int> TrustedRoots = new(PathComparer);

    static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>
    ///     Marks an open store root as allowed to be a link itself. Dispose the result when the
    ///     store closes.
    /// </summary>
    public static IDisposable TrustRoot(string root)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        TrustedRoots.AddOrUpdate(fullRoot, 1, static (_, count) => count + 1);
        return new RootTrust(fullRoot);
    }

    sealed class RootTrust(string root) : IDisposable
    {
        int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            while (TrustedRoots.TryGetValue(root, out var count))
            {
                if (count <= 1
                        ? TrustedRoots.TryRemove(new KeyValuePair<string, int>(root, count))
                        : TrustedRoots.TryUpdate(root, count - 1, count))
                {
                    return;
                }
            }
        }
    }

    /// <summary>Throws when any entry beneath <paramref name="root" /> is a link.</summary>
    public static void EnsureTreeHasNoLinks(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
        {
            return;
        }

        var links = new FileSystemEnumerable<string>(
            root,
            static (ref entry) => entry.ToFullPath(),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
                IgnoreInaccessible = false
            })
        {
            ShouldIncludePredicate = static (ref entry) => IsLink(entry.Attributes),
            ShouldRecursePredicate = static (ref entry) =>
                entry.IsDirectory && !IsLink(entry.Attributes)
        };
        foreach (var link in links)
        {
            throw CreateException(link);
        }
    }

    /// <summary>Throws when <paramref name="path" /> or its parent directory is a link.</summary>
    public static void EnsureNotLink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (parent is not null &&
            !TrustedRoots.ContainsKey(Path.TrimEndingDirectorySeparator(parent)))
        {
            EnsureNotLinkEntry(parent);
        }

        EnsureNotLinkEntry(fullPath);
    }

    static void EnsureNotLinkEntry(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        if (IsLink(attributes))
        {
            throw CreateException(path);
        }
    }

    static bool IsLink(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0;

    static PantsInvalidPathException CreateException(string path) =>
        new($"Storage path '{path}' is a symbolic link or reparse point, which Pants refuses.");
}
