namespace Cntryl.Pants.Storage;

/// <summary>Controls leveled compaction planning.</summary>
/// <param name="MaximumInputFiles">
///     Target ceiling on the files a single compaction consumes. Advisory rather than absolute: a
///     plan's target-level span must stay complete for the output not to overlap files left behind,
///     so when a wide overlap closure cannot fit, the planner narrows its source set instead. One
///     source file plus its complete target span is always planned even if that exceeds this value,
///     because declining to plan would let L0 debt grow without bound.
/// </param>
public sealed record PantsCompactionConfiguration(
    long L0SizeTriggerBytes = 4L * 1024 * 1024,
    int L0FileCountTrigger = 4,
    int MaximumInputFiles = 64,
    int LevelMultiplier = 10,
    long L1TargetSizeBytes = 40L * 1024 * 1024,
    int MaximumLevels = 7,
    long? TargetSstSizeBytes = null,
    bool BackgroundEnabled = true);
