using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Cntryl.Pants.Tests;

sealed class RuntimeMeterMeasurements : IDisposable
{
    readonly IReadOnlySet<string> _names;
    readonly ConcurrentDictionary<string, long> _measurements =
        new(StringComparer.Ordinal);
    readonly MeterListener _listener = new();
    int _hasTags;

    public RuntimeMeterMeasurements(IReadOnlySet<string> names)
    {
        _names = names;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PantsDiagnostics.Meter.Name &&
                _names.Contains(instrument.Name))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            _measurements.AddOrUpdate(
                instrument.Name,
                measurement,
                (_, current) => checked(current + measurement));
            if (!tags.IsEmpty)
            {
                Volatile.Write(ref _hasTags, 1);
            }
        });
        _listener.Start();
    }

    public bool HasTags => Volatile.Read(ref _hasTags) != 0;

    public long this[string name] => _measurements.GetValueOrDefault(name);

    public async ValueTask WaitForAsync(
        IReadOnlySet<string> names,
        TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        while (names.Any(name => this[name] == 0))
        {
            if (Stopwatch.GetElapsedTime(started) >= timeout)
            {
                throw new TimeoutException("Runtime meter signals were not emitted in time.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    public void Dispose() => _listener.Dispose();
}
