namespace Cntryl.Pants.Storage.Internal.Manifest;

sealed record ManifestReadSnapshot(
    ulong LastPersistedSequence,
    ulong NextWalSequence,
    IReadOnlyDictionary<uint, ulong> NextSstSequences,
    IReadOnlyList<FileMeta> Files,
    IReadOnlyList<ColumnFamilyMeta> ColumnFamilies)
{
    public static ManifestReadSnapshot Create(ManifestState manifest) => new(
        manifest.LastPersistedSequence,
        manifest.NextWalSeq,
        new Dictionary<uint, ulong>(manifest.NextSstSeqs),
        manifest.Files.Select(static file => file.Clone()).ToArray(),
        manifest.ColumnFamilies.Select(static family => family.Clone()).ToArray());
}
