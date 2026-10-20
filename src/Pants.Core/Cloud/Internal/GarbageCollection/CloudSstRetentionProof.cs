namespace Cntryl.Pants.Cloud.Internal.GarbageCollection;

sealed record CloudSstRetentionProof(
    IReadOnlySet<string> ProtectedNames,
    IReadOnlyDictionary<string, string> PartialCompactionOutputVersions,
    IReadOnlyDictionary<uint, ulong> RemoteNextSstSequences,
    IReadOnlyList<CloudObjectIdentityGuard> AuthorityGuards,
    IDisposable? reservation = null) : IDisposable
{
    IDisposable? _reservation = reservation;

    public void Dispose() => Interlocked.Exchange(ref _reservation, null)?.Dispose();
}
