namespace Cntryl.Pants.DependencyInjection.Options;

public sealed class PantsCloudLocationOptions
{
    public string? Prefix { get; set; }

    public PantsCloudProviderOptions? Provider { get; set; }
}
