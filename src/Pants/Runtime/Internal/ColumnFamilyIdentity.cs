namespace Cntryl.Pants.Runtime.Internal;

readonly record struct ColumnFamilyIdentity(uint Id, string Name, int Generation);
