namespace Cntryl.Pants;

public sealed record PantsCloudStorageLocation(
    PantsCloudProviderConfiguration Provider,
    string Prefix);
