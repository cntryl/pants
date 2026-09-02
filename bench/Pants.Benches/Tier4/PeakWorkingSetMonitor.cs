using System.Diagnostics;

namespace Cntryl.Pants.Benches.Tier4;

sealed class PeakWorkingSetMonitor : IAsyncDisposable
{
    static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(5);
    readonly CancellationTokenSource _cancellation = new();
    readonly Process _process;
    readonly Task _sampling;
    long _peakBytes;
    int _stopped;

    public PeakWorkingSetMonitor(Process process)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        Sample();
        _sampling = Task.Run(() => SampleAsync(_cancellation.Token));
    }

    public long PeakBytes => Interlocked.Read(ref _peakBytes);

    public async ValueTask StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        await _sampling.ConfigureAwait(false);
        Sample();
        _cancellation.Dispose();
    }

    public ValueTask DisposeAsync() => StopAsync();

    async Task SampleAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !HasExited())
            {
                Sample();
                await Task.Delay(SampleInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    bool HasExited()
    {
        try
        {
            return _process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    void Sample()
    {
        try
        {
            _process.Refresh();
            var candidate = _process.WorkingSet64;
            try
            {
                candidate = Math.Max(candidate, _process.PeakWorkingSet64);
            }
            catch (NotSupportedException)
            {
                // Fall back to the current-working-set sample on platforms without a peak counter.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Fall back when only the peak counter is unavailable for this process.
            }

            while (true)
            {
                var current = Interlocked.Read(ref _peakBytes);
                if (candidate <= current ||
                    Interlocked.CompareExchange(ref _peakBytes, candidate, current) == current)
                {
                    return;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the liveness check and the sample.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The OS no longer exposes counters for the exited process.
        }
    }
}
