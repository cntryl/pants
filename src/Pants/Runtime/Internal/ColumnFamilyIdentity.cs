namespace Cntryl.Pants;

internal readonly record struct ColumnFamilyIdentity(uint Id, string Name, int Generation);
