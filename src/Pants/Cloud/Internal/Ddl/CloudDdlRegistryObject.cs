namespace Cntryl.Pants.Cloud.Internal.Ddl;

sealed record CloudDdlRegistryObject(
    CloudDdlRegistry Registry,
    string Version);
