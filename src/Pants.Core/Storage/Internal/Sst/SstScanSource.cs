namespace Cntryl.Pants.Storage.Internal.Sst;

/// <summary>
///     One SST's contribution to a scan's k-way merge: a leased reader (kept alive/pinned for the
///     duration of the scan via <see cref="SstReaderCache" />) plus a bound-clamped block iterator
///     over it. Disposing releases the lease, not the underlying reader (which the cache still owns).
/// </summary>
sealed class SstScanSource(SstReaderLease lease, SstBlockIterator iterator) : IDisposable
{
    public SstBlockIterator Iterator { get; } = iterator;

    public IReadOnlyList<RangeTombstone> RangeTombstones { get; } = lease.Reader.RangeTombstones;

    public void Dispose()
    {
        Iterator.Dispose();
        lease.Dispose();
    }
}
