namespace Cntryl.Pants.Storage.Internal.Compaction;

sealed class CompactionInputProtectionScope : IDisposable
{
    readonly List<IDisposable> _leases = [];

    public void Add(IDisposable? lease)
    {
        if (lease is not null)
        {
            _leases.Add(lease);
        }
    }

    public void Dispose()
    {
        for (var index = _leases.Count - 1; index >= 0; index--)
        {
            _leases[index].Dispose();
        }

        _leases.Clear();
    }
}
