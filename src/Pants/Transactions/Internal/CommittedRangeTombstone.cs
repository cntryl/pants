namespace Cntryl.Pants;

internal sealed record CommittedRangeTombstone(
    byte[] Start,
    byte[] EndExclusive,
    long WriteSequence);
