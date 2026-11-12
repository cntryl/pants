namespace Cntryl.Pants.Storage.Internal;

/// <summary>
///     Rejects a staged operation that cannot fit in a decoded SST block.
/// </summary>
/// <remarks>
///     The transaction may commit without WAL, inside a batch or as spilled records. Its chosen
///     encoder checks actual framing and decoded-value limits before appending each frame. A conservative
///     bound for every possible WAL envelope is not a staging limit: compression and framing can
///     make an SST-legal operation fit the selected route.
/// </remarks>
static class EntryAdmission
{
    public static void ValidatePointWrite(int keyLength, int? valueLength)
    {
        SstCodec.ValidateEntrySize(keyLength, valueLength ?? 0);
    }

    public static void ValidateRangeDelete(int startLength, int endLength)
    {
        SstCodec.ValidateRangeTombstoneSize(startLength, endLength);
    }
}
