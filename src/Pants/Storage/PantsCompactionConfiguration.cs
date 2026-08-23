namespace Cntryl.Pants.Storage;

/// <summary>Controls leveled compaction planning.</summary>
public sealed record PantsCompactionConfiguration(
    long L0SizeTriggerBytes = 4L * 1024 * 1024,
    int L0FileCountTrigger = 4,
    int MaximumInputFiles = 64,
    int LevelMultiplier = 10,
    long L1TargetSizeBytes = 40L * 1024 * 1024,
    int MaximumLevels = 7,
    long? TargetSstSizeBytes = null);
