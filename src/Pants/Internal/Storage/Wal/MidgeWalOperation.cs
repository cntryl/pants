namespace Pants;

internal enum MidgeWalOperation : byte
{
    Put = 0,
    Insert = 1,
    Delete = 2,
    DeleteRange = 3,
    TransactionBatch = 6
}
