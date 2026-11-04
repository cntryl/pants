namespace Cntryl.Pants.Cloud.Internal.GarbageCollection;

sealed record CloudSstRetentionProof(
    IReadOnlySet<string> ProtectedNames,
    IReadOnlyDictionary<string, string> PartialCompactionOutputVersions,
    IReadOnlyDictionary<uint, ulong> RemoteNextSstSequences,
    IReadOnlyList<CloudObjectIdentityGuard> AuthorityGuards,
    IDisposable? reservation = null,
    IReadOnlyDictionary<string, FileMeta>? RetiredSsts = null,
    Action<FileMeta>? AcknowledgeRetiredSst = null,
    Func<FileMeta, bool>? ValidateRetiredSst = null,
    ResourceBudget? ValidationBudget = null) : IDisposable
{
    IDisposable? _reservation = reservation;

    public void Dispose() => Interlocked.Exchange(ref _reservation, null)?.Dispose();
}
