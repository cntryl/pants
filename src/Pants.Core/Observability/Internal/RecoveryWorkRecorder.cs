namespace Cntryl.Pants.Observability.Internal;

/// <summary>Batches completed work; neither idle time nor error cleanup can emit a batch.</summary>
sealed class RecoveryWorkRecorder(RecoveryWorkSink sink, string stage, TimeProvider timeProvider)
{
    static readonly TimeSpan PartialReportInterval = TimeSpan.FromMilliseconds(500);
    long _bytes;
    long _frames;
    long _operations;
    long _reportedAt = timeProvider.GetTimestamp();

    public void Completed(long bytes = 0, long frames = 0, long operations = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        ArgumentOutOfRangeException.ThrowIfNegative(operations);
        if (bytes == 0 && frames == 0 && operations == 0)
        {
            return;
        }

        _bytes = SaturatingAdd(_bytes, bytes);
        _frames = SaturatingAdd(_frames, frames);
        _operations = SaturatingAdd(_operations, operations);
        if (_bytes >= 64 * 1024 || _frames >= 64 || _operations >= 64 ||
            timeProvider.GetElapsedTime(_reportedAt) >= PartialReportInterval)
        {
            Finish();
        }
    }

    public void Finish()
    {
        if (_bytes == 0 && _frames == 0 && _operations == 0)
        {
            return;
        }

        sink.Record(new RecoveryWorkCompleted(stage, _bytes, _frames, _operations));
        _bytes = 0;
        _frames = 0;
        _operations = 0;
        _reportedAt = timeProvider.GetTimestamp();
    }

    static long SaturatingAdd(long current, long addition) =>
        current > long.MaxValue - addition ? long.MaxValue : current + addition;
}
