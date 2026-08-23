namespace Cntryl.Pants.Cloud.Internal.GarbageCollection;

sealed record CloudSstRetentionProof(
    IReadOnlySet<string> ProtectedNames,
    IReadOnlyDictionary<uint, ulong> RemoteNextSstSequences,
    IReadOnlyList<CloudObjectIdentityGuard> AuthorityGuards);
