namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class DeleteCloudSstBeforeEvictionFailpointHandler(string databasePath) : IFailpointHandler
{
    int _armed = 1;

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != Failpoint.BeforeHybridSstEviction ||
            Interlocked.Exchange(ref _armed, 0) == 0)
        {
            return;
        }

        var cloudSstDirectory = Path.Combine(databasePath, "cloud_store", "sst");
        if (!Directory.Exists(cloudSstDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(
                     cloudSstDirectory,
                     "*.sst",
                     SearchOption.TopDirectoryOnly))
        {
            File.Delete(path);
        }
    }
}
