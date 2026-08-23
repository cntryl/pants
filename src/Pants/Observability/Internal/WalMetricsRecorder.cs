namespace Cntryl.Pants;

sealed class WalMetricsRecorder(RuntimeTelemetry telemetry)
{
    public void RecordAppend(TimeSpan elapsed) => telemetry.RecordWalAppend(elapsed);

    public void RecordFlush() => telemetry.RecordWalFlush();

    public void RecordFsync(TimeSpan elapsed, long sequence) =>
        telemetry.RecordWalFsyncBoundary(elapsed, sequence);
}
