namespace Cntryl.Pants.Storage.Internal.Hybrid;

interface IHybridCacheStore
{
    long LocalCommittedBytes { get; }

    IReadOnlyList<HybridLocalSst> GetLocalManifestSsts();

    bool IsSstLocal(string name);

    ValueTask VerifyRemoteSstMatchesLocalAsync(
        string name,
        CancellationToken cancellationToken);

    void EvictLocalSst(string name);

    ValueTask HydrateLocalSstAsync(string name, CancellationToken cancellationToken);
}
