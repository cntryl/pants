namespace Cntryl.Pants.Cloud.Internal.GarbageCollection;

sealed record CloudCompactionOutputSet(
    uint ColumnFamilyId,
    IReadOnlySet<string> RemovedNames,
    IReadOnlySet<string> AddedNames);
