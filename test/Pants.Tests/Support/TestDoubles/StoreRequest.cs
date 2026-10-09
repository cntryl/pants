namespace Cntryl.Pants.Support.TestDoubles;

readonly record struct StoreRequest(StoreOperation Operation, string ObjectKey, ulong Offset, int Length);
