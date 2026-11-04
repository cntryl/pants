namespace Cntryl.Pants.Storage.Internal.Accounting;

sealed class MetadataAccountingHandle(MetadataAccountingState state)
{
    public MetadataAccountingSnapshot Snapshot() => state.Snapshot();
}
