namespace Pants;

public abstract record PantsStorageConfiguration
{
    private PantsStorageConfiguration()
    {
    }

    public sealed record InMemory : PantsStorageConfiguration;

    public sealed record Local(string Path) : PantsStorageConfiguration;

    public sealed record Cloud(
        string LocalCachePath,
        PantsCloudStorageTopology Topology) : PantsStorageConfiguration;

    public sealed record SimulatedCloud(
        string LocalCachePath,
        string Bucket,
        string Prefix,
        long? LocalStorageBudgetBytes = null) : PantsStorageConfiguration;
}
