namespace Cntryl.Pants.Storage.Internal.Wal;

sealed class WalCommitRollbackException(
    Exception commitFailure,
    Exception rollbackFailure)
    : Exception(
        "The failed WAL commit could not be rolled back safely.",
        new AggregateException(commitFailure, rollbackFailure));
