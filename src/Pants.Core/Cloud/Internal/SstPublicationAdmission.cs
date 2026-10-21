namespace Cntryl.Pants.Cloud.Internal;

/// <summary>
///     Admits SST publication memory (the upload body and its readback) against the shared
///     maintenance pool before the file is read. Runtime mirror publications can defer when the
///     gate is busy, so they do not occupy the cloud worker that compaction may need for manifest
///     publication. Other callers retain FIFO waiting. Compaction output publication already owns
///     the gate and uses <see cref="WithinCompactionRound(ResourceBudget)" />.
/// </summary>
sealed class SstPublicationAdmission
{
    readonly ResourceBudget _budget;
    readonly MaintenanceMemoryGate? _gate;
    readonly bool _deferWhenBusy;

    internal ResourceBudget Budget => _budget;

    SstPublicationAdmission(ResourceBudget budget, MaintenanceMemoryGate? gate, bool deferWhenBusy = false)
    {
        _budget = budget;
        _gate = gate;
        _deferWhenBusy = deferWhenBusy;
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
    ///     Admission for runtime mirror operations that must not wait on an actor-owned cloud
    ///     worker while compaction waits for the actor to publish its manifest.
    /// </summary>
    public static SstPublicationAdmission DeferredWhenBusy(
        ResourceBudget budget,
        MaintenanceMemoryGate gate) =>
        new(budget, gate, true);

    /// <summary>
    ///     Admission for compaction outputs, published by a compaction round that already holds the
    ///     gate after its merge buffers were released. It must not enter the gate again.
    /// </summary>
    public static SstPublicationAdmission WithinCompactionRound(ResourceBudget budget) =>
        new(budget, null);

    /// <summary>
    ///     Reserves the publication envelope of a <paramref name="sizeBytes" /> SST. Compaction
    ///     partitions are sized to fit. When the envelope exceeds the whole pool, admission fails
    ///     before the caller reads the SST; its local output and compaction intent remain available
    ///     for retry with a larger pool or a smaller partition.
    /// </summary>
    public ValueTask<IDisposable> AdmitAsync(long sizeBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = ImmutablePublicationEnvelope.For(sizeBytes);
        if (bytes > _budget.Limit)
        {
            throw PantsException.ResourceLimit(
                $"Publishing an SST of {sizeBytes} bytes needs a {bytes}-byte maintenance " +
                $"envelope; the pool holds {_budget.Limit} bytes.");
        }

        if (_gate is null)
        {
            return ValueTask.FromResult(_budget.Reserve(bytes));
        }

        if (!_deferWhenBusy)
        {
            return AdmitAfterGateAsync(bytes, cancellationToken);
        }

        if (!_gate.TryEnter(out var hold))
        {
            throw new SstPublicationDeferredException();
        }

        return ValueTask.FromResult<IDisposable>(CreateAdmission(bytes, hold!));
    }

    async ValueTask<IDisposable> AdmitAfterGateAsync(long bytes, CancellationToken cancellationToken)
    {
        var hold = await _gate!.EnterAsync(cancellationToken).ConfigureAwait(false);
        return CreateAdmission(bytes, hold);
    }

    Admission CreateAdmission(long bytes, IDisposable hold)
    {
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
