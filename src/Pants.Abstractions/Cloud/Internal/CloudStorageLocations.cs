using System.Collections.Immutable;

namespace Cntryl.Pants.Cloud.Internal;

static class CloudStorageLocations
{
    public static IReadOnlyList<Item> Unique(PantsCloudStorageTopology topology)
    {
        var unique = new List<(PantsCloudStorageLocation Location, List<PantsCloudStorageRole> Roles)>();
        Add(topology.Wal, PantsCloudStorageRole.Wal);
        Add(topology.Sst, PantsCloudStorageRole.Sst);
        Add(topology.Control, PantsCloudStorageRole.Control);
        return unique
            .Select(static item => new Item(item.Location, [.. item.Roles]))
            .ToArray();

        void Add(PantsCloudStorageLocation location, PantsCloudStorageRole role)
        {
            var index = unique.FindIndex(item => item.Location == location);
            if (index < 0)
            {
                unique.Add((location, [role]));
            }
            else
            {
                unique[index].Roles.Add(role);
            }
        }
    }

    internal readonly record struct Item(
        PantsCloudStorageLocation Location,
        ImmutableArray<PantsCloudStorageRole> Roles);
}
