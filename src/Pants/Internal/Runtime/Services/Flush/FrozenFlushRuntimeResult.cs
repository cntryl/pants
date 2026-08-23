namespace Pants;

sealed record FrozenFlushRuntimeResult(
    FlushPublicationPlan? PublicationPlan,
    bool PersistenceAnomaly);
