namespace Cntryl.Pants.Storage.Internal.Accounting;

enum MetadataOrigin
{
    OrdinaryLocalFlush,
    CloudFlush,
    Recovery,
    Bootstrap,
    Ddl,
    CompactionBeforeGc,
    Administration,
    Shutdown,
    Unclassified
}
