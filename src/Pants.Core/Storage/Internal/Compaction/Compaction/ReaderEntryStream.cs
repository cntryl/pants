namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

/// <summary>Streams one already-open reader's entries; the caller keeps ownership of the reader.</summary>
sealed class ReaderEntryStream(SstReader reader, ResourceBudget? resourceBudget) : ICompactionEntryStream
{
    readonly SstBlockIterator _iterator = SstBlockIterator.Create(
        reader,
        PantsScanDirection.Forward,
        resourceBudget: resourceBudget);

    public SstEntry Current => _iterator.Current;

    public bool MoveNext() => _iterator.MoveNext();

    public void Dispose() => _iterator.Dispose();
}
