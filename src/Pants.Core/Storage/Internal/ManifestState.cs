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

    /// <summary>
    ///     Copies the state so an edit sequence can be applied to the copy and committed back only once
    ///     the whole sequence has succeeded.
    /// </summary>
    public ManifestState Clone() => new()
    {
        LastPersistedSequence = LastPersistedSequence,
        Files = [.. Files.Select(static file => file.Clone())],
        ColumnFamilies = [.. ColumnFamilies.Select(static family => family.Clone())],
        CloudCheckpoint = CloudCheckpoint,
        NextWalSeq = NextWalSeq,
        NextSstSeqs = new Dictionary<uint, ulong>(NextSstSeqs),
        EditCheckpointId = EditCheckpointId
    };

    /// <summary>Replaces this state's contents with those of <paramref name="source" />.</summary>
    public void CopyFrom(ManifestState source)
    {
        LastPersistedSequence = source.LastPersistedSequence;
        Files = source.Files;
        ColumnFamilies = source.ColumnFamilies;
        CloudCheckpoint = source.CloudCheckpoint;
        NextWalSeq = source.NextWalSeq;
        NextSstSeqs = source.NextSstSeqs;
        EditCheckpointId = source.EditCheckpointId;
    }
}
