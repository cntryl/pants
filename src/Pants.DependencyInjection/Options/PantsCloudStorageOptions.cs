namespace Cntryl.Pants.Options;

public sealed class PantsCloudStorageOptions
{
    public PantsCloudLocationOptions? Shared { get; set; }

    public PantsCloudLocationOptions? Wal { get; set; }

    public PantsCloudLocationOptions? Sst { get; set; }

    public PantsCloudLocationOptions? Control { get; set; }
}
