namespace Cntryl.Pants;

sealed record CloudDdlRegistryObject(
    CloudDdlRegistry Registry,
    string Version);
