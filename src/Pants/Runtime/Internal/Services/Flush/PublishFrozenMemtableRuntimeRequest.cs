namespace Cntryl.Pants;

sealed record PublishFrozenMemtableRuntimeRequest(
    FrozenMemtableFlush Frozen,
    FlushPublicationPlan? PublicationPlan) : FlushRuntimeRequest;
