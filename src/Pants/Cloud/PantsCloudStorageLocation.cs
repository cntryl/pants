namespace Cntryl.Pants.Cloud;

public sealed record PantsCloudStorageLocation(
    PantsCloudProviderConfiguration Provider,
    string Prefix);
