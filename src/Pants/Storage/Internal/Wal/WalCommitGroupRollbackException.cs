namespace Cntryl.Pants;

sealed class WalCommitGroupRollbackException(
    Exception groupFailure,
    Exception rollbackFailure)
    : Exception(
        "The failed WAL commit group could not be rolled back safely.",
        new AggregateException(groupFailure, rollbackFailure));
