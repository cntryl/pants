namespace Cntryl.Pants.Transactions.Internal;

/// <summary>
///     Resolves the manifest-visible SST files a point read or scan needs to consider for a given
///     <see cref="DatabaseVersion" />, replacing "walk the in-memory keyspace" with "select the
///     candidate on-disk sources" — the shared first step both the point-read path and the scan
///     k-way merge build on. Only file *selection* lives here; decoding/merging candidate blocks
///     into a visible value is the caller's job (via <see cref="SstBlockIterator" />/<see cref="SstReader" />).
/// </summary>
static class SnapshotReadPath
{
    /// <summary>
    ///     SST files for <paramref name="family" /> whose key range could contain <paramref name="key" />,
    ///     newest (highest <see cref="FileMeta.SstSequence" />) first so a caller can stop at the first
    ///     definitive hit.
    /// </summary>
    public static IReadOnlyList<FileMeta> ResolveCandidateFilesForPoint(
        DatabaseVersion snapshot,
        ColumnFamilyIdentity family,
        ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var keyCopy = key.ToArray();
        return snapshot.GetVisibleFiles(family.Id)
            .Where(file => LocalDiskStore.IsWithinFileRange(file, keyCopy))
            .OrderByDescending(static file => file.SstSequence)
            .ToArray();
    }

    /// <summary>
    ///     SST files for <paramref name="family" /> that overlap <c>[startInclusive, endExclusive)</c>
    ///     (either bound <c>null</c> means unbounded on that side), in manifest order — the caller's
    ///     k-way merge determines relative priority by sequence, not by this list's order.
    /// </summary>
    public static IReadOnlyList<FileMeta> ResolveCandidateFilesForRange(
        DatabaseVersion snapshot,
        ColumnFamilyIdentity family,
        byte[]? startInclusive,
        byte[]? endExclusive)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.GetVisibleFiles(family.Id)
            .Where(file => Overlaps(file, startInclusive, endExclusive))
            .ToArray();
    }

    static bool Overlaps(FileMeta file, byte[]? startInclusive, byte[]? endExclusive)
    {
        if (file.SmallestKey is null || file.LargestKey is null)
        {
            return false;
        }

        var smallest = LocalDiskStore.GetMetadataKey(file.SmallestKey);
        var largest = LocalDiskStore.GetMetadataKey(file.LargestKey);
        return (endExclusive is null || smallest.AsSpan().SequenceCompareTo(endExclusive) < 0) &&
               (startInclusive is null || largest.AsSpan().SequenceCompareTo(startInclusive) >= 0);
    }
}
