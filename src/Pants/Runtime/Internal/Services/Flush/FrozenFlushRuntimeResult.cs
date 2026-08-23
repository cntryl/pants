namespace Cntryl.Pants.Runtime.Internal.Services.Flush;

sealed record FrozenFlushRuntimeResult(
    FlushPublicationPlan? PublicationPlan,
    bool PersistenceAnomaly);
