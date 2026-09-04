using System.Text;

namespace Cntryl.Pants.Transactions.Spill;

sealed class TransactionSpillCrashFailpointHandler(
    Failpoint target,
    string sentinelPath,
    string scenario,
    string trigger) : IFailpointHandler
{
    int _armed = 1;

    public void Hit(Failpoint failpoint)
    {
        if (failpoint != target || Interlocked.Exchange(ref _armed, 0) != 1)
        {
            return;
        }

        var sentinel = Encoding.UTF8.GetBytes($"scenario={scenario}\ntrigger={trigger}\n");
        using (var stream = new FileStream(
                   sentinelPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.Read,
                   4_096,
                   FileOptions.WriteThrough))
        {
            stream.Write(sentinel);
            stream.Flush(true);
        }

        var parent = Path.GetDirectoryName(sentinelPath) ??
                     throw new InvalidOperationException("The crash-trigger sentinel has no parent directory.");
        AtomicStagedFile.FlushDirectory(parent);

        Environment.FailFast($"Injected crash at {trigger} for scenario {scenario}.");
    }
}
