namespace Cntryl.Pants.Storage.Internal.Sst;

/// <summary>
///     Splits a point read's candidate files into the groups it may stop between.
/// </summary>
/// <remarks>
///     Level order guarantees that every source older than one holding the key holds only older
///     versions of it, range tombstones included, so a point read walks its sources newest first
///     and stops at the first group that yields a version or a covering range tombstone. A group
///     is one level-zero file whose sequence range is strictly ordered against its neighbours, or
///     one whole deeper level. Files that cannot be ordered against each other are visited
///     together, so the newest version still wins and the equal-sequence content check still sees
///     both.
/// </remarks>
static class PointReadGroups
{
    /// <summary>
    ///     The candidates with level zero first and each deeper level after it, keeping the given
    ///     order inside a level. A level-zero file published while a compaction ran can carry a
    ///     lower file sequence than that compaction's output yet hold newer data, so file sequence
    ///     alone does not order sources across levels.
    /// </summary>
    public static IReadOnlyList<FileMeta> InLevelOrder(IReadOnlyList<FileMeta> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var ordered = true;

        for (var index = 1; index < candidates.Count && ordered; index++)
        {
            ordered = candidates[index - 1].Level <= candidates[index].Level;
        }

        // OrderBy is a stable sort, so files keep their relative order within a level.

        return ordered ? candidates : [.. candidates.OrderBy(static file => file.Level)];
    }

    /// <summary>
    ///     End (exclusive) of the group that starts at <paramref name="start" /> in level-ordered
    ///     <paramref name="candidates" />.
    /// </summary>
    /// <remarks>
    ///     A level-zero group closes only when every level-zero file after it is strictly older
    ///     than everything in it, judged by recorded sequence ranges rather than by list order. If
    ///     any level-zero file lacks a sequence range, all of level zero is one group.
    /// </remarks>
    public static int GroupEnd(IReadOnlyList<FileMeta> candidates, int start)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var level = candidates[start].Level;
        var levelEnd = start + 1;

        while (levelEnd < candidates.Count && candidates[levelEnd].Level == level)
        {
            levelEnd++;
        }


        if (level != 0)
        {
            return levelEnd;
        }

        for (var index = start; index < levelEnd; index++)
        {
            if (candidates[index].SmallestSequence is null || candidates[index].LargestSequence is null)
            {
                return levelEnd;
            }
        }

        var groupOldest = ulong.MaxValue;

        for (var index = start; index < levelEnd; index++)
        {
            groupOldest = Math.Min(groupOldest, candidates[index].SmallestSequence!.Value);
            var restIsOlder = true;

            for (var later = index + 1; later < levelEnd && restIsOlder; later++)
            {
                restIsOlder = candidates[later].LargestSequence!.Value < groupOldest;
            }


            if (restIsOlder)
            {
                return index + 1;
            }
        }


        return levelEnd;
    }
}
