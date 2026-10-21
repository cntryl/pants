using System.Text.Json.Serialization;
using Cntryl.Pants.Storage.Internal.Manifest;

namespace Cntryl.Pants.Storage.Internal;

sealed class ManifestState
{
    public ulong LastPersistedSequence { get; set; }

    public List<FileMeta> Files { get; set; } = [];

    public List<ColumnFamilyMeta> ColumnFamilies { get; set; } = [];

    public object? CloudCheckpoint { get; set; }

    public ulong NextWalSeq { get; set; } = 1;

    [JsonPropertyName("pending_wal_segment_id")]
    public ulong? PendingWalSegmentId { get; set; }

    [JsonPropertyName("wal_sequence_reservation_version")]
    public uint WalSequenceReservationVersion { get; set; }

    [JsonPropertyName("reserved_commit_sequence")]
    public ulong ReservedCommitSequence { get; set; }

    public Dictionary<uint, ulong> NextSstSeqs { get; set; } = [];

    [JsonPropertyName("flushed_through")]
    [JsonConverter(typeof(FlushFrontierMapConverter))]
    public Dictionary<uint, ulong> FlushedThrough { get; set; } = new() { [0] = 0 };

    public ulong WalHistoryStart { get; set; } = 1;

    public ulong EditCheckpointId { get; set; }

    public static ManifestState CreateInitial() => new()
    {
        NextSstSeqs = new Dictionary<uint, ulong>(),
        FlushedThrough = new Dictionary<uint, ulong> { [0] = 0 },
        WalSequenceReservationVersion = 1
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
        PendingWalSegmentId = PendingWalSegmentId,
        WalSequenceReservationVersion = WalSequenceReservationVersion,
        ReservedCommitSequence = ReservedCommitSequence,
        NextSstSeqs = new Dictionary<uint, ulong>(NextSstSeqs),
        FlushedThrough = new Dictionary<uint, ulong>(FlushedThrough),
        WalHistoryStart = WalHistoryStart,
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
        PendingWalSegmentId = source.PendingWalSegmentId;
        WalSequenceReservationVersion = source.WalSequenceReservationVersion;
        ReservedCommitSequence = source.ReservedCommitSequence;
        NextSstSeqs = source.NextSstSeqs;
        FlushedThrough = source.FlushedThrough;
        WalHistoryStart = source.WalHistoryStart;
        EditCheckpointId = source.EditCheckpointId;
    }
}
