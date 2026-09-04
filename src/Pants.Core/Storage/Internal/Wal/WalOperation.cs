namespace Cntryl.Pants.Storage.Internal.Wal;

enum WalOperation : byte
{
    Put = 0,
    Insert = 1,
    Delete = 2,
    DeleteRange = 3,
    TransactionBegin = 4,
    TransactionCommit = 5,
    TransactionBatch = 6
}
