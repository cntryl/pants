namespace Cntryl.Pants.Runtime.Internal.Services.Flush;

sealed record PublishFrozenMemtableRuntimeRequest(
    FrozenMemtableFlush Frozen,
    FlushPublicationPlan? PublicationPlan) : FlushRuntimeRequest;
