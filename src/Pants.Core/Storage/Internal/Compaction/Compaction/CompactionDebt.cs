namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

/// <summary>
///     Describes whether a column family's compaction debt is clear: no L0 files remain and no
///     inner level is over its size target. A manual drain is complete only when this holds.
/// </summary>
static class CompactionDebt
{
    public static bool IsClear(
        IReadOnlyList<FileMeta> files,
        uint columnFamilyId,
        PantsCompactionConfiguration configuration)
    {
        var familyFiles = files.Where(file => file.ColumnFamilyId == columnFamilyId).ToArray();
        if (familyFiles.Any(static file => file.Level == 0))
        {
            return false;
        }

        for (uint level = 1; level < configuration.MaximumLevels - 1; level++)
        {
            var levelSize = familyFiles
                .Where(file => file.Level == level)
                .Aggregate(0UL, static (total, file) => checked(total + file.SizeBytes));
            if (levelSize > LeveledCompactionPlanner.LevelTargetBytes(configuration, level))
            {
                return false;
            }
        }

        return true;
    }
}
