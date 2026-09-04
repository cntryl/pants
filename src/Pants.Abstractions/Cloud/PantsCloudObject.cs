namespace Cntryl.Pants.Cloud;

public sealed record PantsCloudObject(
    ReadOnlyMemory<byte> Data,
    string Version);
