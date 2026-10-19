namespace Cntryl.Pants.Cloud.Internal.Ddl;

sealed class CloudDdlRegistryObject(
    CloudDdlRegistry registry,
    string version,
    ReadOnlyMemory<byte> data = default,
    IDisposable? memoryReservation = null) : IDisposable
{
    IDisposable? _memoryReservation = memoryReservation;

    public CloudDdlRegistry Registry { get; } = registry;

    public string Version { get; } = version;

    public ReadOnlyMemory<byte> Data { get; } = data;

    public void Dispose() => Interlocked.Exchange(ref _memoryReservation, null)?.Dispose();
}
