namespace Cntryl.Pants.Cloud;

public static class PantsCloudObjectLayout
{
    public const string WalPrefix = "wal/";
    public const string WalCatalogObjectKey = "wal/publication-catalog.v1.json";
    public const string WalCatalogMirrorObjectKey = "wal/publication-catalog.v1.mirror.json";
    public const string SstPrefix = "sst/";
    public const string MetadataPrefix = "metadata/";
    public const string DdlRegistryObjectKey = "metadata/ddl.registry.json";
    public const string LeaseObjectKey = "midge_primary_lease.json";

    public static string WalSegmentObjectKey(ulong writerEpoch, ulong segmentId) =>
        $"wal/epochs/{writerEpoch:00000000000000000000}/{segmentId:00000000000000000000}.wal";
}
