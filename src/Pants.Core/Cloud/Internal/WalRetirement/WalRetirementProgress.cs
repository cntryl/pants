namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

/// <summary>
///     Keeps segment proofs across retirement turns, so a turn cut short by its quantum, a provider
///     timeout or memory pressure resumes where it stopped. Before reuse a proof is revalidated: the
///     WAL object must still be the pinned version and every manifest file it relied on must still
///     be published unchanged. Progress is process-local; after a restart retirement revalidates
///     from durable metadata.
/// </summary>
sealed class WalRetirementProgress
{
    readonly Dictionary<ulong, WalSegmentProof> _proofs = [];

    public int Count => _proofs.Count;

    /// <summary>Drops proofs for segments the catalog no longer holds.</summary>
    public void RetainOnly(IReadOnlyCollection<ulong> catalogSegmentIds)
    {
        foreach (var segmentId in _proofs.Keys.Where(id => !catalogSegmentIds.Contains(id)).ToArray())
        {
            _proofs.Remove(segmentId);
        }
    }

    /// <summary>The proof to continue for <paramref name="segment" />, restarting it when stale.</summary>
    public WalSegmentProof Resume(
        ProviderPublishedWalSegment segment,
        string version,
        WalCoverageIndex coverage)
    {
        if (_proofs.TryGetValue(segment.SegmentId, out var proof) &&
            proof.Segment == segment &&
            StringComparer.Ordinal.Equals(proof.Version, version) &&
            coverage.StillContains(proof.CoveringFiles))
        {
            return proof;
        }

        proof = new WalSegmentProof(segment, version);
        _proofs[segment.SegmentId] = proof;
        return proof;
    }

    public void Forget(ulong segmentId) => _proofs.Remove(segmentId);
}
