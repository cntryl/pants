namespace Cntryl.Pants.Cloud.Internal;

/// <summary>
///     Admits SST publication memory (the upload body and its readback) against the shared
///     maintenance pool before the file is read. Admission first enters the
///     <see cref="MaintenanceMemoryGate" /> in arrival order, so a publication waits for an in-flight
///     compaction round (whose SST and intent stay pending) instead of failing, and never makes a
///     concurrent merge fail. Inside the gate the pool is exclusively the holder's, so the
///     reservation itself cannot contend.
/// </summary>
sealed class SstPublicationAdmission
{
    readonly ResourceBudget _budget;
    readonly MaintenanceMemoryGate? _gate;

    SstPublicationAdmission(ResourceBudget budget, MaintenanceMemoryGate? gate)
    {
        _budget = budget;
        _gate = gate;
    }

    /// <summary>
    ///     The compaction partition size whose publication envelope fits the pool alongside live
    ///     merge inputs.
    /// </summary>
    public long PartitionTargetBytes => ImmutablePublicationEnvelope.PartitionTarget(_budget.Limit);

    /// <summary>Admission for publications outside compaction, such as the flush mirror.</summary>
    public static SstPublicationAdmission Gated(ResourceBudget budget, MaintenanceMemoryGate gate) =>
        new(budget, gate);

    /// <summary>
    ///     Admission for compaction outputs, published by a compaction round that already holds the
    ///     gate after its merge buffers were released. It must not enter the gate again.
    /// </summary>
    public static SstPublicationAdmission WithinCompactionRound(ResourceBudget budget) =>
        new(budget, null);

    /// <summary>
    ///     Reserves the publication envelope of a <paramref name="sizeBytes" /> SST. Compaction
    ///     partitions are sized to fit; an SST whose envelope exceeds the whole pool (a flush output
    ///     sized by its memtable, an indivisible oversized entry, or a degenerate pool) claims the
    ///     entire pool rather than becoming permanently inadmissible.
    /// </summary>
    public async ValueTask<IDisposable> AdmitAsync(long sizeBytes, CancellationToken cancellationToken)
    {
        var bytes = Math.Min(ImmutablePublicationEnvelope.For(sizeBytes), _budget.Limit);
        if (_gate is null)
        {
            return _budget.Reserve(bytes);
        }

        var hold = await _gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new Admission(_budget.Reserve(bytes), hold);
        }
        catch
        {
            hold.Dispose();
            throw;
        }
    }

    sealed class Admission(IDisposable reservation, IDisposable hold) : IDisposable
    {
        public void Dispose()
        {
            reservation.Dispose();
            hold.Dispose();
        }
    }
}
