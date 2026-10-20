using System.Collections.Concurrent;

namespace Cntryl.Pants.Cloud.Internal;

sealed class ProviderSstIdentityRegistry
{
    readonly ConcurrentDictionary<string, Identity> _identities = new(StringComparer.Ordinal);

    internal void Record(string name, ulong sizeBytes, string version)
    {
        var identity = new Identity(sizeBytes, version);
        var recorded = _identities.GetOrAdd(name, identity);
        if (recorded != identity)
        {
            throw new PantsFencedException(
                $"Immutable cloud SST '{name}' was observed with conflicting publication identities.");
        }
    }

    internal bool TryGet(string name, out Identity identity) =>
        _identities.TryGetValue(name, out identity);

    internal readonly record struct Identity(ulong SizeBytes, string Version);
}
