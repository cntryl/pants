namespace Cntryl.Pants.Storage.Internal.Cache;

/// <summary>
///     The manifest identity of one SST: its name plus the column family, SST sequence, size and
///     content checksum the manifest recorded for it. Reader and block cache entries are keyed by
///     this rather than the file name alone, so a name reused for different content never serves
///     the previous file's readers or blocks.
/// </summary>
readonly record struct SstFileIdentity(
    string Name,
    uint ColumnFamilyId,
    ulong SstSequence,
    ulong SizeBytes,
    uint? ContentCrc32C)
{
    public static SstFileIdentity Of(FileMeta file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new SstFileIdentity(
            file.Name,
            file.ColumnFamilyId,
            file.SstSequence,
            file.SizeBytes,
            file.ContentCrc32C);
    }
}
