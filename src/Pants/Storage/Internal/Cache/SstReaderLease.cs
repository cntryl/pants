namespace Cntryl.Pants.Storage.Internal.Cache;

sealed class SstReaderLease(SstReader reader, Action release) : IDisposable
{
    Action? _release = release;

    public SstReader Reader { get; } = reader;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
