namespace Cntryl.Pants.Options;

public sealed class PantsCloudLocationOptions
{
    public string? Prefix { get; set; }

    public PantsCloudProviderOptions? Provider { get; set; }
}
