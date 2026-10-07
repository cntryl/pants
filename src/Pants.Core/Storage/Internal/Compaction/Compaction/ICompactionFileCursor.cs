namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

/// <summary>
///     One input SST opened for a compaction merge, whether it lives on local disk or is read
///     through bounded remote ranges without ever being staged locally.
/// </summary>
interface ICompactionFileCursor : IDisposable
{
    IReadOnlyList<RangeTombstone> RangeTombstones { get; }

    SstEntry Current { get; }

    bool MoveNext();
}
