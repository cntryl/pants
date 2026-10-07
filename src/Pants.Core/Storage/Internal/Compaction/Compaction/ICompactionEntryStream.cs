namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

/// <summary>
///     One ascending-key (descending-sequence within a key) entry stream feeding a compaction
///     merge. A stream may span several files, opening them one at a time.
/// </summary>
interface ICompactionEntryStream : IDisposable
{
    SstEntry Current { get; }

    bool MoveNext();
}
