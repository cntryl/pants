namespace Cntryl.Pants.Runtime.Internal.Services.Flush;

sealed class FrozenFlushRuntimeException(
    FrozenFlushRuntimeResult result,
    Exception innerException)
    : Exception("Frozen memtable publication failed.", innerException)
{
    public FrozenFlushRuntimeResult Result { get; } = result;
}
