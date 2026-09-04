namespace Cntryl.Pants.Storage.Internal;

interface ILocalFlushStore
{
    void Flush(RuntimeState state);

    void Flush(RuntimeState state, ColumnFamilyIdentity identity);

    FlushPublicationPlan BuildFrozenFlushPlan(FrozenMemtableFlush frozen);

    FlushPublicationResult PublishFrozenFlushPlan(
        FrozenMemtableFlush frozen,
        FlushPublicationPlan plan);
}
