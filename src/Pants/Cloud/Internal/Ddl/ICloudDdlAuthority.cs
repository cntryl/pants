namespace Cntryl.Pants;

interface ICloudDdlAuthority
{
    ValueTask FenceDdlRegistryAsync(
        CloudDdlRegistry bootstrap,
        CancellationToken cancellationToken);

    ValueTask<CloudDdlRegistryObject?> ReadDdlRegistryAsync(
        CancellationToken cancellationToken);

    ValueTask<bool> CompareExchangeDdlRegistryAsync(
        CloudDdlRegistry registry,
        string? expectedVersion,
        CancellationToken cancellationToken);
}
