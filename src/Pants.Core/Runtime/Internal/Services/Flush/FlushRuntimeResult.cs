namespace Cntryl.Pants.Runtime.Internal.Services.Flush;

readonly record struct FlushRuntimeResult(
    FrozenFlushRuntimeResult? Frozen = null);
