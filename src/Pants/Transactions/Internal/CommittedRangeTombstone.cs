namespace Cntryl.Pants.Transactions.Internal;

sealed record CommittedRangeTombstone(
    byte[] Start,
    byte[] EndExclusive,
    long WriteSequence);
