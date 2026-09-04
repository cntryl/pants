namespace Cntryl.Pants.Options;

public sealed class PantsCompactionOptions
{
    public long L0SizeTriggerBytes { get; set; } = 4L * 1024 * 1024;

    public int L0FileCountTrigger { get; set; } = 4;

    public int MaximumInputFiles { get; set; } = 64;

    public int LevelMultiplier { get; set; } = 10;

    public long L1TargetSizeBytes { get; set; } = 40L * 1024 * 1024;

    public int MaximumLevels { get; set; } = 7;

    public long? TargetSstSizeBytes { get; set; }
}
