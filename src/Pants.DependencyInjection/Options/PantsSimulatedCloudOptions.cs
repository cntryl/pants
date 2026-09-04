namespace Cntryl.Pants.Options;

public sealed class PantsSimulatedCloudOptions
{
    public string? Bucket { get; set; }

    public string? Prefix { get; set; }

    public long? LocalStorageBudgetBytes { get; set; }
}
