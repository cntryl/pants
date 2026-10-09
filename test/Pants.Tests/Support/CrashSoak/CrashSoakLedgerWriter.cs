using System.Text;

namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>
///     The child's append-only record of what it dispatched and what the engine answered. Each line
///     reaches the operating system before the call it describes returns, so a process kill cannot
///     lose an entry: a dispatch is recorded before the commit starts and an acknowledgement only
///     after the commit returns.
/// </summary>
sealed class CrashSoakLedgerWriter : IDisposable
{
    readonly Lock _gate = new();
    readonly FileStream _stream;

    public CrashSoakLedgerWriter(string path)
    {
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }

    public void Dispatched(CrashSoakOperation operation) => Append($"D {operation.Id}");

    public void Acked(CrashSoakOperation operation) => Append($"A {operation.Id}");

    public void Failed(CrashSoakOperation operation, string reason) => Append($"F {operation.Id} {reason}");

    /// <summary>Records an exception the workload did not expect; the operation's outcome stays unknown.</summary>
    public void Errored(CrashSoakOperation operation, Exception exception) =>
        Append($"E {operation.Id} {exception.GetType().Name}: {OneLine(exception.Message)}");

    public void Completed() => Append("C");

    public void Killing(string reason) => Append($"K {OneLine(reason)}");

    public void Dispose() => _stream.Dispose();

    void Append(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_gate)
        {
            _stream.Write(bytes);
            _stream.Flush(false);
        }
    }

    static string OneLine(string text) => text.Replace('\n', ' ').Replace('\r', ' ');
}
