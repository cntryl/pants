namespace Cntryl.Pants.Storage.Internal;

interface ILocalFlushStore
{
    void Flush(MetadataOrigin origin);

    void Flush(ColumnFamilyIdentity identity, MetadataOrigin origin);

    FlushPublicationPlan BuildFrozenFlushPlan(FrozenMemtableFlush frozen);

    FlushPublicationResult PublishFrozenFlushPlan(
        FrozenMemtableFlush frozen,
        FlushPublicationPlan plan);
}
