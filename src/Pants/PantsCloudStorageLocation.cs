namespace Pants;

public sealed record PantsCloudStorageLocation(
    PantsCloudProviderConfiguration Provider,
    string Prefix);
