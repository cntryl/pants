namespace Cntryl.Pants;

sealed record FlushColumnFamilyRuntimeRequest(
    PantsRuntimeState State,
    ColumnFamilyIdentity ColumnFamily) : FlushRuntimeRequest;
