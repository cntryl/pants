using System.Text.Json;

namespace Cntryl.Pants.Storage.Internal.Manifest;

sealed record ManifestReadSnapshot(
    ulong LastPersistedSequence,
    ulong NextWalSequence,
    IReadOnlyDictionary<uint, ulong> NextSstSequences,
    IReadOnlyDictionary<uint, ulong> FlushedThrough,
    ulong WalHistoryStart,
    IReadOnlyList<FileMeta> Files,
    IReadOnlyList<ColumnFamilyMeta> ColumnFamilies,
    object? CloudCheckpoint,
    ulong EditCheckpointId)
{
    public static ManifestReadSnapshot Create(ManifestState manifest) => new(
        manifest.LastPersistedSequence,
        manifest.NextWalSeq,
        new Dictionary<uint, ulong>(manifest.NextSstSeqs),
        new Dictionary<uint, ulong>(manifest.FlushedThrough),
        manifest.WalHistoryStart,
        manifest.Files.Select(static file => file.Clone()).ToArray(),
        manifest.ColumnFamilies.Select(static family => family.Clone()).ToArray(),
        CloneCloudCheckpoint(manifest.CloudCheckpoint),
        manifest.EditCheckpointId);

    static object? CloneCloudCheckpoint(object? checkpoint) => checkpoint switch
    {
        null => null,
        JsonElement element => element.Clone(),
        _ => JsonSerializer.SerializeToElement(checkpoint)
    };
}
