namespace Cntryl.Pants.Storage.Internal.Hybrid;

interface IHybridCacheStore
{
    long LocalCommittedBytes { get; }

    IReadOnlyList<HybridLocalSst> GetLocalManifestSsts();

    /// <summary>
    ///     The manifest's recorded size for <paramref name="name" />, whether or not it is resident.
    ///     Hydration needs this to reserve budget before downloading.
    /// </summary>
    bool TryGetManifestSstSizeBytes(string name, out long sizeBytes);

    bool IsSstLocal(string name);

    ValueTask VerifyRemoteSstMatchesLocalAsync(
        string name,
        CancellationToken cancellationToken);

    void EvictLocalSst(string name);

    ValueTask HydrateLocalSstAsync(string name, CancellationToken cancellationToken);
}
