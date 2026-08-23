namespace Cntryl.Pants.Cloud;

public static class PantsCloudObjectLayout
{
    public const string WalPrefix = "wal/";
    public const string WalCatalogObjectKey = "wal/publication-catalog.v1.json";
    public const string SstPrefix = "sst/";
    public const string MetadataPrefix = "metadata/";
    public const string DdlRegistryObjectKey = "metadata/ddl.registry.json";
    public const string LeaseObjectKey = "midge_primary_lease.json";

    public static string WalSegmentObjectKey(ulong writerEpoch, ulong segmentId)
    {
        if (writerEpoch == 0 || segmentId == 0)
        {
            throw PantsException.InvalidArgument(
                "Cloud WAL writer epochs and segment IDs must be greater than zero.");
        }

        return $"wal/epochs/{writerEpoch:00000000000000000000}/{segmentId:00000000000000000000}.wal";
    }
}
