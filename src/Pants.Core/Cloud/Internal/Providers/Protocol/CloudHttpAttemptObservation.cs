using System.Diagnostics;
using Cntryl.Pants.Observability;

namespace Cntryl.Pants.Cloud.Internal.Providers.Protocol;

sealed class CloudHttpAttemptObservation : IDisposable
{
    const string TagPrefix = "pants.cloud_io.";
    readonly Activity? _activity;
    readonly long _started = Stopwatch.GetTimestamp();
    long _responseBodyBytes;
    int _status;
    bool _transportError;
    bool _cancelled;

    public CloudHttpAttemptObservation(HttpRequestMessage request)
    {
        _activity = PantsDiagnostics.ActivitySource.StartActivity(
            "Pants.Cloud.HttpAttempt",
            ActivityKind.Client);
        if (_activity is null)
        {
            return;
        }

        _activity.SetTag(TagPrefix + "method", request.Method.Method);
        _activity.SetTag(TagPrefix + "range", request.Headers.Range is not null);
        _activity.SetTag(TagPrefix + "request_body_bytes", request.Content?.Headers.ContentLength ?? 0L);
    }

    public bool IsEnabled => _activity is not null;

    public void RecordStatus(int status) => _status = status;

    public void RecordResponseBodyBytes(long bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref _responseBodyBytes, bytes);
        }
    }

    public void RecordTransportError() => _transportError = true;

    public void RecordCancellation() => _cancelled = true;

    public void Dispose()
    {
        if (_activity is null)
        {
            return;
        }

        _activity.SetTag(TagPrefix + "response_body_bytes", Interlocked.Read(ref _responseBodyBytes));
        _activity.SetTag(TagPrefix + "status", _status);
        _activity.SetTag(TagPrefix + "transport_error", _transportError);
        _activity.SetTag(TagPrefix + "cancelled", _cancelled);
        _activity.SetTag(
            TagPrefix + "elapsed_ns",
            checked((long)Stopwatch.GetElapsedTime(_started).TotalNanoseconds));
        _activity.Dispose();
    }
}
