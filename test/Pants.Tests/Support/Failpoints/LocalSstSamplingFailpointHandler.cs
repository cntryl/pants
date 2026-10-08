namespace Cntryl.Pants.Support.Failpoints;

/// <summary>Records which SST files are on local disk each time a compaction output becomes durable.</summary>
sealed class LocalSstSamplingFailpointHandler(string databasePath) : IFailpointHandler
{
    readonly Lock _gate = new();
    readonly List<string[]> _samples = [];

    public IReadOnlyList<string[]> Samples
    {
        get
        {
            lock (_gate)
            {
                return [.. _samples];
            }
        }
    }

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != Failpoint.AfterCompactionOutputDurable)
        {
            return;
        }

        var sstDirectory = Path.Combine(databasePath, "sst");
        var names = Directory.Exists(sstDirectory)
            ? Directory.GetFiles(sstDirectory, "*.sst").Select(static path => Path.GetFileName(path)).ToArray()
            : [];
        lock (_gate)
        {
            _samples.Add(names);
        }
    }
}
