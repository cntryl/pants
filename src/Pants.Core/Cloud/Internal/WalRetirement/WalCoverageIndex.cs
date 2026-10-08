namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

/// <summary>
///     The manifest files that can prove WAL coverage, grouped by column family. Coverage is decided
///     from each file's recorded key and sequence bounds alone, so proving a segment reads no SST
///     bytes, and only the files that actually cover a record are later checked remotely.
/// </summary>
sealed class WalCoverageIndex
{
    readonly Dictionary<uint, FileMeta[]> _byFamily;
    readonly Dictionary<string, FileMeta> _byName;

    public WalCoverageIndex(ManifestState manifest)
    {
        _byName = new Dictionary<string, FileMeta>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            _byName[file.Name] = file;
        }

        _byFamily = manifest.Files
            .Where(static file =>
                file.HasTrustedKeyBounds() &&
                file.SmallestSequence.HasValue &&
                file.LargestSequence.HasValue)
            .GroupBy(static file => file.ColumnFamilyId)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
    }

    /// <returns>A manifest file whose bounds cover the mutation, or null when none does.</returns>
    public FileMeta? FindCovering(WalMutation mutation)
    {
        if (!_byFamily.TryGetValue(mutation.ColumnFamilyId, out var files))
        {
            return null;
        }

        foreach (var file in files)
        {
            if (CloudWalCoverageValidator.Covers(file, mutation))
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>
    ///     Whether every file a resumed proof relied on is still in this manifest with the same
    ///     identity and bounds, so the proof's semantic progress remains valid. Files appended since
    ///     do not matter.
    /// </summary>
    public bool StillContains(IReadOnlyDictionary<string, FileMeta> files)
    {
        foreach (var (name, file) in files)
        {
            if (!_byName.TryGetValue(name, out var current) || !SameFile(file, current))
            {
                return false;
            }
        }

        return true;
    }

    static bool SameFile(FileMeta left, FileMeta right) =>
        left.ColumnFamilyId == right.ColumnFamilyId &&
        left.SizeBytes == right.SizeBytes &&
        left.ContentCrc32C == right.ContentCrc32C &&
        left.KeyBoundsComplete == right.KeyBoundsComplete &&
        left.SmallestSequence == right.SmallestSequence &&
        left.LargestSequence == right.LargestSequence &&
        left.SmallestKey.AsSpan().SequenceEqual(right.SmallestKey) &&
        left.LargestKey.AsSpan().SequenceEqual(right.LargestKey);
}
