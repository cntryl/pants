namespace Cntryl.Pants.DependencyInjection.Options;

public sealed class PantsStorageOptions
{
    public PantsStorageKind Kind { get; set; } = PantsStorageKind.InMemory;

    public string? Path { get; set; }

    public PantsSimulatedCloudOptions? SimulatedCloud { get; set; }

    public PantsCloudStorageOptions? Cloud { get; set; }
}
