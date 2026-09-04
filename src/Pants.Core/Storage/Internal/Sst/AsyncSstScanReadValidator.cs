namespace Cntryl.Pants.Storage.Internal.Sst;

/// <summary>
///     Records the blocks consumed by the asynchronous scan pipeline. Block integrity is checked by
///     <see cref="AsyncSstReader" /> as each source advances, so this validator observes that work
///     instead of issuing a duplicate synchronous read over cloud-backed sources.
/// </summary>
sealed class AsyncSstScanReadValidator(
    RuntimeTelemetry telemetry,
    IReadOnlyList<AsyncSstScanSource> sources) : IScanReadValidator
{
    int _disposed;

    public void ValidateKey(ReadOnlySpan<byte> key)
    {
    }

    public void Complete()
    {
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        telemetry.RecordSstScan(
            sources.Count,
            sources.Sum(static source => source.CandidateBlockCount),
            sources.Sum(static source => source.DataBlocksRead),
            0,
            0,
            0);
    }
}
