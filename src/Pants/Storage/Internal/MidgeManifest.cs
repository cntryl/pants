namespace Cntryl.Pants;

internal sealed class MidgeManifest
{
    public ulong LastPersistedSequence { get; set; }

    public List<MidgeFileMeta> Files { get; set; } = [];

    public List<MidgeColumnFamilyMeta> ColumnFamilies { get; set; } = [];

    public object? CloudCheckpoint { get; set; }

    public ulong NextWalSeq { get; set; } = 1;

    public Dictionary<uint, ulong> NextSstSeqs { get; set; } = [];

    public ulong EditCheckpointId { get; set; }

    public static MidgeManifest CreateInitial() => new()
    {
        NextSstSeqs = new Dictionary<uint, ulong> { [0] = 1 }
    };
}
