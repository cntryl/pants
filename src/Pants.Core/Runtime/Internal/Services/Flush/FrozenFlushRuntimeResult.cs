namespace Cntryl.Pants.Runtime.Internal.Services.Flush;

sealed record FrozenFlushRuntimeResult(
    FlushPublicationPlan? PublicationPlan,
    bool PersistenceAnomaly,
    long? PublicationStartedTimestamp = null,
    long? PublicationCompletedTimestamp = null,
    long? FlushId = null);
