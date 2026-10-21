namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

/// <summary>
///     The manifest SSTs and durable family drops that can prove WAL coverage. SST coverage is
///     decided from recorded key and sequence bounds alone, so proving a segment reads no SST bytes,
///     and only the files that actually cover a record are later checked remotely.
/// </summary>
sealed class WalCoverageIndex
{
    readonly Dictionary<uint, FileMeta[]> _byFamily;
    readonly Dictionary<uint, ulong> _droppedFamilyDropSequences;
    readonly Dictionary<string, FileMeta> _byName;

    public WalCoverageIndex(ManifestState manifest)
    {
        _byName = new Dictionary<string, FileMeta>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            _byName[file.Name] = file;
        }

        _droppedFamilyDropSequences = manifest.ColumnFamilies
            .Where(static family => family.DeletedAt.HasValue && family.DropSequence.HasValue)
            .ToDictionary(static family => family.Id, static family => family.DropSequence!.Value);
        var deletedFamilyIds = manifest.ColumnFamilies
            .Where(static family => family.DeletedAt.HasValue)
            .Select(static family => family.Id)
            .ToHashSet();
        var maximumActiveFamilyFrontier = manifest.FlushedThrough
            .Where(entry => !deletedFamilyIds.Contains(entry.Key))
            .Select(static entry => entry.Value)
            .DefaultIfEmpty(0UL)
            .Max();
        var maximumDroppedFamilySequence = _droppedFamilyDropSequences.Values
            .DefaultIfEmpty(0UL)
            .Max();
        MaximumSequenceWithDurableEvidence = Math.Max(
            manifest.LastPersistedSequence,
            Math.Max(maximumActiveFamilyFrontier, maximumDroppedFamilySequence));
        _byFamily = manifest.Files
            .Where(static file =>
                file.HasTrustedKeyBounds() &&
                file.SmallestSequence.HasValue &&
                file.LargestSequence.HasValue)
            .GroupBy(static file => file.ColumnFamilyId)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
    }

    /// <summary>
    ///     A coarse upper bound for WAL segments worth checking. Final retirement still requires
    ///     per-mutation SST coverage or a matching durable family drop.
    /// </summary>
    public ulong MaximumSequenceWithDurableEvidence { get; }

    /// <summary>
    ///     Finds durable evidence that a mutation no longer needs replay: either an SST covers it,
    ///     or an authoritative family drop discards it at or after its sequence.
    /// </summary>
    public bool TryFindCovering(
        WalMutation mutation,
        out FileMeta? file,
        out ulong? droppedFamilyDropSequence)
    {
        if (_droppedFamilyDropSequences.TryGetValue(
                mutation.ColumnFamilyId,
                out var dropSequence) &&
            mutation.Sequence <= dropSequence)
        {
            file = null;
            droppedFamilyDropSequence = dropSequence;
            return true;
        }

        if (_byFamily.TryGetValue(mutation.ColumnFamilyId, out var files))
        {
            foreach (var candidate in files)
            {
                if (CloudWalCoverageValidator.Covers(candidate, mutation))
                {
                    file = candidate;
                    droppedFamilyDropSequence = null;
                    return true;
                }
            }
        }

        file = null;
        droppedFamilyDropSequence = null;
        return false;
    }

    /// <summary>
    ///     Whether every SST and family drop a resumed proof relied on is still in this manifest
    ///     with the same identity and bounds, so the proof's semantic progress remains valid.
    ///     Unrelated files appended since do not matter.
    /// </summary>
    public bool StillContains(
        IReadOnlyDictionary<string, FileMeta> files,
        IReadOnlyDictionary<uint, ulong> droppedFamilies)
    {
        foreach (var (name, file) in files)
        {
            if (!_byName.TryGetValue(name, out var current) || !SameFile(file, current))
            {
                return false;
            }
        }

        foreach (var (familyId, dropSequence) in droppedFamilies)
        {
            if (!_droppedFamilyDropSequences.TryGetValue(familyId, out var currentDropSequence) ||
                currentDropSequence != dropSequence)
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
