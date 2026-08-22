namespace Pants;

sealed record CloudSstRetentionProof(
    IReadOnlySet<string> ProtectedNames,
    IReadOnlyDictionary<uint, ulong> RemoteNextSstSequences,
    IReadOnlyList<CloudObjectIdentityGuard> AuthorityGuards);
