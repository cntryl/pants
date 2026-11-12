using Cntryl.Pants.Cloud.Internal.Objects;
using Cntryl.Pants.Storage.Internal;

namespace Cntryl.Pants.Cloud.Internal;

/// <summary>
///     Reads startup control metadata only after reserving its encoded bytes and parse workspace.
///     Successful results keep their reservations until the hydration caller finishes using the
///     decoded metadata and releases the set.
/// </summary>
sealed class ProviderCloudControlMetadataReadSet(
    ICloudObjectStore store,
    ResourceBudget budget,
    RecoveryWorkSink? recoveryWork = null,
    Action? metadataAdmitted = null) : IDisposable
{
    public const int ReadRangeBytes = 64 * 1024;
    public const int MaximumWholeObjectBytes = 64 * 1024;

    const int WorkspaceMultiplier = 16;
    const long FixedWorkspaceBytes = 256;

    readonly Lock _gate = new();
    readonly List<IDisposable> _reservations = [];
    int _disposed;

    readonly ICloudObjectStore _store = store ?? throw new ArgumentNullException(nameof(store));
    readonly ResourceBudget _budget = budget ?? throw new ArgumentNullException(nameof(budget));

    public async ValueTask<CloudObject?> ReadAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_store is SimulatedCloudObjectStore simulated)
        {
            return ReadSimulatedControlMetadata(simulated, objectKey, cancellationToken);
        }
        var metadata = await _store.HeadAsync(objectKey, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        IDisposable? reservation = ReserveWorkspace(metadata.SizeBytes, objectKey);
        try
        {
            metadataAdmitted?.Invoke();
            var value = metadata.SizeBytes == 0
                ? await ReadWholeObjectAsync(objectKey, metadata, cancellationToken)
                    .ConfigureAwait(false)
                : await ReadRangesAsync(objectKey, metadata, cancellationToken)
                    .ConfigureAwait(false);
            Retain(reservation);
            reservation = null;
            return value;
        }
        finally
        {
            reservation?.Dispose();
        }
    }

    CloudObject? ReadSimulatedControlMetadata(
        SimulatedCloudObjectStore simulated,
        string objectKey,
        CancellationToken cancellationToken)
    {
        IDisposable? reservation = null;
        try
        {
            var value = simulated.ReadControlMetadata(objectKey,
                size =>
                {
                    reservation = ReserveWorkspace(size, objectKey);
                    metadataAdmitted?.Invoke();
                },
                cancellationToken);
            if (value is not null)
            {
                if (!value.Data.IsEmpty)
                {
                    recoveryWork?.Record(new RecoveryWorkCompleted("remote_read", value.Data.Length, 0, 0));
                }
                Retain(reservation!);
                reservation = null;
            }
            return value;
        }
        finally
        {
            reservation?.Dispose();
        }
    }

    IDisposable ReserveWorkspace(ulong sizeBytes, string objectKey)
    {
        if (sizeBytes > int.MaxValue)
        {
            throw new PantsResourceLimitException(
                $"Cloud control object '{objectKey}' exceeds the supported byte-array size.");
        }

        var workspaceBytes = GetWorkspaceBytes(sizeBytes, objectKey);
        if (workspaceBytes > _budget.Limit)
        {
            throw new PantsResourceLimitException(
                $"Reading cloud control object '{objectKey}' needs {workspaceBytes} bytes of " +
                $"maintenance memory; the pool holds {_budget.Limit}.");
        }
        return _budget.Reserve(workspaceBytes);
    }

    public void ReleaseReservations()
    {
        IDisposable[] reservations;
        lock (_gate)
        {
            reservations = _reservations.ToArray();
            _reservations.Clear();
        }

        foreach (var reservation in reservations)
        {
            reservation.Dispose();
        }
    }

    public IDisposable TransferReservations()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var reservations = _reservations.ToArray();
            _reservations.Clear();
            return new ReservationSet(reservations);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ReleaseReservations();
        GC.SuppressFinalize(this);
    }

    async ValueTask<CloudObject> ReadRangesAsync(
        string objectKey,
        CloudObjectMetadata metadata,
        CancellationToken cancellationToken)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)metadata.SizeBytes));
        for (var offset = 0; offset < bytes.Length;)
        {
            var length = Math.Min(ReadRangeBytes, bytes.Length - offset);
            CloudObject? range;
            try
            {
                range = _store is CloudObjectStore builtInStore
                    ? await builtInStore.GetRangeAsync(
                            objectKey,
                            checked((ulong)offset),
                            length,
                            metadata,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await _store.GetRangeAsync(
                            objectKey,
                            checked((ulong)offset),
                            length,
                            cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (PantsNotSupportedException exception)
            {
                if (metadata.SizeBytes > MaximumWholeObjectBytes)
                {
                    throw new PantsResourceLimitException(
                        $"Cloud control object '{objectKey}' is too large for a store without " +
                        "bounded ranged reads.",
                        exception);
                }

                return await ReadWholeObjectAsync(objectKey, metadata, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (range is null || !StringComparer.Ordinal.Equals(range.Version, metadata.Version))
            {
                throw new PantsBusyException(
                    $"Cloud control object '{objectKey}' changed during a bounded read.");
            }

            if (range.Data.Length != length)
            {
                throw new PantsIOException(
                    $"Cloud control object '{objectKey}' did not return the requested range.");
            }

            range.Data.Span.CopyTo(bytes.AsSpan(offset, length));
            offset += length;
            recoveryWork?.Record(new RecoveryWorkCompleted("remote_read", length, 0, 0));
        }

        return new CloudObject(bytes, metadata.Version);
    }

    async ValueTask<CloudObject> ReadWholeObjectAsync(
        string objectKey,
        CloudObjectMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (metadata.SizeBytes > MaximumWholeObjectBytes)
        {
            throw new PantsResourceLimitException(
                $"Cloud control object '{objectKey}' exceeds the {MaximumWholeObjectBytes}-byte " +
                "limit for a whole-object read.");
        }

        var value = await _store.GetAsync(objectKey, cancellationToken).ConfigureAwait(false);
        if (value is null || !StringComparer.Ordinal.Equals(value.Version, metadata.Version))
        {
            throw new PantsBusyException(
                $"Cloud control object '{objectKey}' changed during a bounded read.");
        }

        if (checked((ulong)value.Data.Length) != metadata.SizeBytes)
        {
            throw new PantsIOException(
                $"Cloud control object '{objectKey}' did not return its declared size.");
        }

        if (!value.Data.IsEmpty)
        {
            recoveryWork?.Record(new RecoveryWorkCompleted("remote_read", value.Data.Length, 0, 0));
        }
        return value;
    }

    static long GetWorkspaceBytes(ulong encodedBytes, string objectKey)
    {
        try
        {
            var encoded = checked((long)encodedBytes);
            return checked((encoded * WorkspaceMultiplier) + FixedWorkspaceBytes);
        }
        catch (OverflowException exception)
        {
            throw new PantsResourceLimitException(
                $"Cloud control object '{objectKey}' exceeds the maintenance memory accounting range.",
                exception);
        }
    }

    void Retain(IDisposable reservation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _reservations.Add(reservation);
        }
    }

    sealed class ReservationSet(IDisposable[] reservations) : IDisposable
    {
        IDisposable[]? _reservations = reservations;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _reservations, null);
            if (current is null)
            {
                return;
            }

            foreach (var reservation in current)
            {
                reservation.Dispose();
            }
        }
    }
}
