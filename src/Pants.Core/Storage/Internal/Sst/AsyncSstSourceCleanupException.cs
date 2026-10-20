namespace Cntryl.Pants.Storage.Internal.Sst;

sealed class AsyncSstSourceCleanupException(
    Exception openException,
    Exception cleanupException)
    : IOException(
        "An async SST source could not be closed after an open failure.",
        new AggregateException(openException, cleanupException));
