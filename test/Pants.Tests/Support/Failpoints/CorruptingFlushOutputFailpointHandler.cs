namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class CorruptingFlushOutputFailpointHandler(string databasePath) : IFailpointHandler
{
    int _hit;

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != Failpoint.AfterFlushOutputDurable ||
            Interlocked.CompareExchange(ref _hit, 1, 0) != 0)
        {
            return;
        }

        var stagingPath = Assert.Single(Directory.GetFiles(
            Path.Combine(databasePath, "sst", ".flush-staging"),
            "*.tmp"));
        using var stream = new FileStream(
            stagingPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var original = stream.ReadByte();
        Assert.NotEqual(-1, original);
        stream.Position = 0;
        stream.WriteByte((byte)(original ^ 0xff));
        stream.Flush(true);
    }
}
