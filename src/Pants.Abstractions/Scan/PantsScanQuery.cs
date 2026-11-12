namespace Cntryl.Pants.Scan;

public sealed record PantsScanQuery
{
    public ReadOnlyMemory<byte>? StartInclusive { get; init; }

    public ReadOnlyMemory<byte>? EndExclusive { get; init; }

    public ReadOnlyMemory<byte>? Prefix { get; init; }

    public PantsScanDirection Direction { get; init; }

    public int? Limit { get; init; }

    /// <summary>
    ///     Controls insertion of scanned SST blocks into the shared block cache. By default,
    ///     scans with an explicit limit of at most 64 rows fill the cache; other scans bypass
    ///     insertion. Both policies can read blocks already in the cache.
    /// </summary>
    public PantsCacheFill? CacheFill { get; init; }
}
