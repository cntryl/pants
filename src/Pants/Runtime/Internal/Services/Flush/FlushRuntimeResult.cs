namespace Cntryl.Pants;

readonly record struct FlushRuntimeResult(
    FrozenFlushRuntimeResult? Frozen = null);
