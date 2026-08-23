namespace Cntryl.Pants.Runtime.Internal.Services.Flush;

sealed record FlushColumnFamilyRuntimeRequest(
    PantsRuntimeState State,
    ColumnFamilyIdentity ColumnFamily) : FlushRuntimeRequest;
