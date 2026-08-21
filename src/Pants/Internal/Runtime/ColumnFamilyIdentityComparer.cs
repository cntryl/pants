namespace Pants;

internal sealed class ColumnFamilyIdentityComparer : IEqualityComparer<ColumnFamilyIdentity>
{
    public static ColumnFamilyIdentityComparer Instance { get; } = new();

    private ColumnFamilyIdentityComparer()
    {
    }

    public bool Equals(ColumnFamilyIdentity x, ColumnFamilyIdentity y) =>
        x.Id == y.Id && x.Name == y.Name && x.Generation == y.Generation;

    public int GetHashCode(ColumnFamilyIdentity value) =>
        HashCode.Combine(value.Id, value.Name, value.Generation);
}
