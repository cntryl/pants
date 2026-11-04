namespace Cntryl.Pants.Runtime.Internal.Services.Flush;

sealed record FlushColumnFamilyRuntimeRequest(
    ColumnFamilyIdentity ColumnFamily,
    MetadataOrigin Origin) : FlushRuntimeRequest;
