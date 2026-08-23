namespace Cntryl.Pants;

internal sealed class DatabaseSnapshot
{
    public DatabaseSnapshot(
        long sequence,
        Dictionary<ColumnFamilyIdentity, SortedDictionary<byte[], CellState>> families,
        Dictionary<string, int> activeColumnFamilyVersions)
    {
        Sequence = sequence;
        Families = families;
        ActiveColumnFamilyVersions = activeColumnFamilyVersions;
    }

    public long Sequence { get; }

    public Dictionary<ColumnFamilyIdentity, SortedDictionary<byte[], CellState>> Families { get; }

    public Dictionary<string, int> ActiveColumnFamilyVersions { get; }
}
