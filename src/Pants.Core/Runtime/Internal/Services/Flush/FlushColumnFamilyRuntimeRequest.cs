namespace Cntryl.Pants.Runtime.Internal.Services.Flush;

sealed record FlushColumnFamilyRuntimeRequest(
    RuntimeState State,
    ColumnFamilyIdentity ColumnFamily) : FlushRuntimeRequest;
