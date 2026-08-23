namespace Cntryl.Pants.Storage.Internal.Flush;

sealed record FrozenMemtableFlush(
    long Id,
    ColumnFamilyIdentity ColumnFamily,
    uint ColumnFamilyId,
    IReadOnlyList<MidgeWalMutation> Operations,
    ulong SstSequence,
    ulong FrontierSequence,
    long SizeBytes)
{
    public string SstName =>
        $"{ColumnFamilyId:000000}_00_{SstSequence:00000000000000000000}.sst";
}
