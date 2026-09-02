using Cntryl.Pants.Runtime.Internal;

namespace Cntryl.Pants.Destroyer.Support;

/// <summary>
/// Flips a byte in the staged flush output the first time it becomes
/// durable, simulating on-disk SST corruption. Ported from
/// <c>test/Pants.Tests/Support/Failpoints/CorruptingFlushOutputFailpointHandler.cs</c>.
/// </summary>
sealed class SstByteCorruptingFailpointHandler(string databasePath) : IFailpointHandler
{
    int _hit;

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != Failpoint.AfterFlushOutputDurable ||
            Interlocked.CompareExchange(ref _hit, 1, 0) != 0)
        {
            return;
        }

        var stagingDirectory = Path.Combine(databasePath, "sst", ".flush-staging");
        var stagingFile = Directory.GetFiles(stagingDirectory, "*.tmp").Single();

        using var stream = new FileStream(
            stagingFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var original = stream.ReadByte();
        if (original < 0)
        {
            return;
        }

        stream.Position = 0;
        stream.WriteByte((byte)(original ^ 0xFF));
        stream.Flush(true);
    }
}
