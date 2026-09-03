namespace Cntryl.Pants.Cloud.Internal;

sealed class SimulatedCloudSstSourceFactory : IAsyncSstSourceFactory
{
    readonly string _sstRoot;

    public SimulatedCloudSstSourceFactory(string localRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localRoot);
        _sstRoot = Path.Combine(Path.GetFullPath(localRoot), "cloud_store", "sst");
    }

    public ValueTask<IAsyncSstSource?> OpenAsync(
        FileMeta file,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateName(file.Name);
        var path = Path.Combine(_sstRoot, file.Name);
        return ValueTask.FromResult<IAsyncSstSource?>(
            File.Exists(path) ? LocalAsyncSstSource.Open(path) : null);
    }

    static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !StringComparer.Ordinal.Equals(Path.GetFileName(name), name))
        {
            throw new PantsCorruptionException("A simulated-cloud SST name is unsafe.");
        }
    }
}
