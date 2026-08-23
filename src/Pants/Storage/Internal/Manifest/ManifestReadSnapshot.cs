namespace Cntryl.Pants;

sealed record ManifestReadSnapshot(
    ulong LastPersistedSequence,
    ulong NextWalSequence,
    IReadOnlyDictionary<uint, ulong> NextSstSequences,
    IReadOnlyList<MidgeFileMeta> Files,
    IReadOnlyList<MidgeColumnFamilyMeta> ColumnFamilies)
{
    public static ManifestReadSnapshot Create(MidgeManifest manifest) => new(
        manifest.LastPersistedSequence,
        manifest.NextWalSeq,
        new Dictionary<uint, ulong>(manifest.NextSstSeqs),
        manifest.Files.Select(static file => file.Clone()).ToArray(),
        manifest.ColumnFamilies.Select(static family => family.Clone()).ToArray());
}
