namespace Cntryl.Pants;

sealed class FrozenFlushRuntimeException(
    FrozenFlushRuntimeResult result,
    Exception innerException)
    : Exception("Frozen memtable publication failed.", innerException)
{
    public FrozenFlushRuntimeResult Result { get; } = result;
}
