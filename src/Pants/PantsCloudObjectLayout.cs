namespace Pants;

public static class PantsCloudObjectLayout
{
    public const string WalPrefix = "wal/";
    public const string WalCatalogObjectKey = "wal/publication-catalog.v1.json";
    public const string SstPrefix = "sst/";
    public const string MetadataPrefix = "metadata/";
    public const string LeaseObjectKey = "midge_primary_lease.json";
}
