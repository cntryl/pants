using System.Globalization;

namespace Pants;

readonly record struct CloudSstIdentity(uint ColumnFamilyId, ulong Sequence)
{
    public static bool TryParse(string name, out CloudSstIdentity identity)
    {
        identity = default;
        var stem = Path.GetFileNameWithoutExtension(name);
        var parts = stem.Split('_');
        if (parts.Length != 3 ||
            !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var familyId) ||
            !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var level) ||
            !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
        {
            return false;
        }

        var canonical = $"{familyId:000000}_{level:00}_{sequence:00000000000000000000}.sst";
        if (!StringComparer.Ordinal.Equals(name, canonical))
        {
            return false;
        }

        identity = new CloudSstIdentity(familyId, sequence);
        return true;
    }
}
