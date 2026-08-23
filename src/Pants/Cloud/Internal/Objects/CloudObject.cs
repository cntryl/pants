namespace Cntryl.Pants.Cloud.Internal.Objects;

sealed record CloudObject(
    ReadOnlyMemory<byte> Data,
    string Version);
