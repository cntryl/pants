namespace Cntryl.Pants.Storage.Internal;

sealed class ManifestState
{
    public ulong LastPersistedSequence { get; set; }

    public List<FileMeta> Files { get; set; } = [];

    public List<ColumnFamilyMeta> ColumnFamilies { get; set; } = [];

    public object? CloudCheckpoint { get; set; }

    public ulong NextWalSeq { get; set; } = 1;

    public Dictionary<uint, ulong> NextSstSeqs { get; set; } = [];

    public ulong EditCheckpointId { get; set; }

    public static ManifestState CreateInitial() => new()
    {
        NextSstSeqs = new Dictionary<uint, ulong>()
    };
}
