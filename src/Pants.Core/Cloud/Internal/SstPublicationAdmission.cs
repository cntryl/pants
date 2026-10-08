namespace Cntryl.Pants.Cloud.Internal;

/// <summary>
///     Admits SST publication memory (the upload body and its readback) against the shared
///     maintenance budget that compaction merges also draw from, before the file is read.
///     Publication that contends with other maintenance waits for room instead of failing, so the
///     SST, and a compaction's durable outputs and intent, stay pending rather than abandoned.
/// </summary>
sealed class SstPublicationAdmission(ResourceBudget budget)
{
    static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(1);
    static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    ///     The compaction partition size whose publication envelope fits the pool alongside live
    ///     merge inputs.
    /// </summary>
    public long PartitionTargetBytes => ImmutablePublicationEnvelope.PartitionTarget(budget.Limit);

    /// <summary>
    ///     Reserves the publication envelope of a <paramref name="sizeBytes" /> SST. Compaction
    ///     partitions are sized to fit; an SST whose envelope exceeds the whole pool (a flush output
    ///     sized by its memtable, an indivisible oversized entry, or a degenerate pool) claims the
    ///     entire pool and runs alone rather than becoming permanently inadmissible.
    /// </summary>
    public async ValueTask<IDisposable> AdmitAsync(long sizeBytes, CancellationToken cancellationToken)
    {
        var bytes = Math.Min(ImmutablePublicationEnvelope.For(sizeBytes), budget.Limit);
        var delay = InitialRetryDelay;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.TryReserve(bytes, out var reservation))
            {
                return reservation;
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaximumRetryDelay.Ticks));
        }
    }
}
