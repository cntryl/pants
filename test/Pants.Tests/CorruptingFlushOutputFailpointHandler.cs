namespace Pants.Tests;

sealed class CorruptingFlushOutputFailpointHandler(string databasePath) : IPantsFailpointHandler
{
    int _hit;

    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint != PantsFailpoint.AfterFlushOutputDurable ||
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
        stream.Flush(flushToDisk: true);
    }
}
