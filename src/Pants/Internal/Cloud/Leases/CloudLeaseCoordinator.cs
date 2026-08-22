namespace Pants;

internal sealed class CloudLeaseCoordinator : IDisposable
{
    readonly ICloudLeaseStore _store;
    readonly IPantsClock _clock;
    readonly string _holderId;
    readonly string _ownerToken = Guid.NewGuid().ToString("N");
    readonly TimeSpan _leaseDuration;
    readonly TimeSpan _clockSkewTolerance;
    readonly Action? _leaseLossCallback;
    readonly SemaphoreSlim _gate = new(1, 1);
    ulong _epoch;
    long _expiresAtUtcTicks;
    long _latestObservedUtcTicks;
    int _lost;
    int _disposed;

    public CloudLeaseCoordinator(
        ICloudLeaseStore store,
        IPantsClock clock,
        string holderId,
        TimeSpan leaseDuration,
        TimeSpan clockSkewTolerance,
        Action? leaseLossCallback = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentException.ThrowIfNullOrWhiteSpace(holderId);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw PantsException.InvalidArgument("Cloud lease duration must be greater than zero.");
        }

        if (clockSkewTolerance < TimeSpan.Zero || clockSkewTolerance >= leaseDuration)
        {
            throw PantsException.InvalidArgument(
                "Cloud lease clock-skew tolerance must be non-negative and shorter than the lease duration.");
        }

        _holderId = holderId;
        _leaseDuration = leaseDuration;
        _clockSkewTolerance = clockSkewTolerance;
        _leaseLossCallback = leaseLossCallback;
    }

    public ulong Epoch => Volatile.Read(ref _epoch);

    public bool IsHealthy
    {
        get
        {
            if (Volatile.Read(ref _lost) != 0 || Epoch == 0)
            {
                return false;
            }

            if (ObserveMonotonicUtcTicks() < Volatile.Read(ref _expiresAtUtcTicks))
            {
                return true;
            }

            LoseLease();
            return false;
        }
    }

    public async ValueTask<ulong> AcquireAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _lost) != 0)
            {
                throw new PantsFencedException("The cloud primary lease coordinator is fenced.");
            }
            var now = ObserveMonotonicUtcNow();
            var current = await _store.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            ulong nextEpoch;
            bool acquired;
            if (current is null)
            {
                nextEpoch = 1;
                acquired = await _store.TryCreateAsync(
                    new CloudLeaseRecord(
                        _holderId,
                        nextEpoch,
                        _ownerToken,
                        now,
                        now + _leaseDuration),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (now <= current.Lease.ExpiresAtUtc + _clockSkewTolerance)
                {
                    throw new PantsLeaseHeldException(
                        $"Cloud primary lease is held at epoch {current.Lease.Epoch}.");
                }

                if (current.Lease.Epoch == ulong.MaxValue)
                {
                    throw new PantsLeaseEpochExhaustedException(
                        "The cloud primary lease epoch is exhausted.");
                }

                nextEpoch = checked(current.Lease.Epoch + 1);
                acquired = await _store.TryReplaceAsync(
                    current.Version,
                    new CloudLeaseRecord(
                        _holderId,
                        nextEpoch,
                        _ownerToken,
                        now,
                        now + _leaseDuration),
                    cancellationToken).ConfigureAwait(false);
            }

            if (!acquired)
            {
                throw new PantsLeaseHeldException("Lost the conditional cloud lease acquisition race.");
            }

            Volatile.Write(ref _expiresAtUtcTicks, (now + _leaseDuration).UtcTicks);
            Volatile.Write(ref _epoch, nextEpoch);
            return nextEpoch;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RenewAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureValid();
            var current = await ReadForRenewalAsync(cancellationToken).ConfigureAwait(false);
            EnsureValid();
            var epoch = Epoch;
            if (!Owns(current.Lease, epoch))
            {
                LoseLease();
                throw new PantsFencedException("The cloud primary lease is owned by another writer.");
            }

            var expiresAt = ObserveMonotonicUtcNow() + _leaseDuration;
            var proposed = current.Lease with { ExpiresAtUtc = expiresAt };
            EnsureValid();
            CloudLeaseSnapshot? confirmed = null;
            try
            {
                var renewed = await _store.TryReplaceAsync(
                    current.Version,
                    proposed,
                    cancellationToken).ConfigureAwait(false);
                if (!renewed)
                {
                    LoseLease();
                    throw new PantsFencedException("The cloud primary lease renewal was fenced.");
                }
            }
            catch (PantsLeaseIndeterminateException error)
            {
                confirmed = await ConfirmIndeterminateRenewalAsync(
                    proposed,
                    error,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!TryAdvanceDeadline(epoch, expiresAt))
            {
                LoseLease();
                await TryExpireLateRenewalAsync(proposed, confirmed, cancellationToken)
                    .ConfigureAwait(false);
                throw new PantsFencedException(
                    "The cloud primary lease renewal completed after its monotonic deadline.");
            }
        }
        catch (PantsException)
        {
            LoseLease();
            throw;
        }
        catch (OperationCanceledException)
        {
            LoseLease();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void EnsureValid()
    {
        if (!IsHealthy)
        {
            LoseLease();
            throw new PantsFencedException("The cloud primary lease is no longer valid.");
        }
    }

    public async ValueTask ReleaseAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _lost) != 0 || Epoch == 0)
            {
                return;
            }

            var current = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (current is null ||
                current.Lease.Epoch != Epoch ||
                !StringComparer.Ordinal.Equals(current.Lease.HolderId, _holderId) ||
                !StringComparer.Ordinal.Equals(current.Lease.OwnerToken, _ownerToken))
            {
                LoseLease();
                return;
            }

            var released = await _store.TryReplaceAsync(
                current.Version,
                current.Lease with
                {
                    ExpiresAtUtc = ObserveMonotonicUtcNow() - _clockSkewTolerance - TimeSpan.FromTicks(1)
                },
                cancellationToken).ConfigureAwait(false);
            if (!released)
            {
                LoseLease();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Volatile.Write(ref _lost, 1);
            _gate.Dispose();
        }
    }

    async ValueTask<CloudLeaseSnapshot> ReadForRenewalAsync(
        CancellationToken cancellationToken)
    {
        var current = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            return current;
        }

        LoseLease();
        throw new PantsFencedException("The cloud primary lease document disappeared.");
    }

    async ValueTask<CloudLeaseSnapshot> ConfirmIndeterminateRenewalAsync(
        CloudLeaseRecord proposed,
        PantsLeaseIndeterminateException writeError,
        CancellationToken cancellationToken)
    {
        CloudLeaseSnapshot? current;
        try
        {
            current = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PantsException readError)
        {
            throw new PantsLeaseIndeterminateException(
                "The cloud primary lease renewal could not be confirmed by readback.",
                new AggregateException(writeError, readError));
        }

        if (current?.Lease != proposed)
        {
            throw new PantsLeaseUnavailableException(
                "The cloud primary lease renewal was not confirmed by readback.",
                writeError);
        }

        return current;
    }

    bool TryAdvanceDeadline(ulong epoch, DateTimeOffset candidate)
    {
        if (Volatile.Read(ref _lost) != 0 || Epoch != epoch)
        {
            return false;
        }

        var currentDeadline = Volatile.Read(ref _expiresAtUtcTicks);
        var now = ObserveMonotonicUtcTicks();
        if (now >= currentDeadline || candidate.UtcTicks <= now)
        {
            return false;
        }

        Volatile.Write(ref _expiresAtUtcTicks, Math.Max(currentDeadline, candidate.UtcTicks));
        return Volatile.Read(ref _lost) == 0;
    }

    async ValueTask TryExpireLateRenewalAsync(
        CloudLeaseRecord proposed,
        CloudLeaseSnapshot? confirmed,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = confirmed ?? await _store.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (current is null || !Owns(current.Lease, proposed.Epoch))
            {
                return;
            }

            await _store.TryReplaceAsync(
                current.Version,
                current.Lease with
                {
                    ExpiresAtUtc = ObserveMonotonicUtcNow() - _clockSkewTolerance - TimeSpan.FromTicks(1)
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is PantsException or OperationCanceledException)
        {
            // A bounded best-effort release must never restore local authority.
        }
    }

    bool Owns(CloudLeaseRecord lease, ulong epoch) =>
        lease.Epoch == epoch &&
        StringComparer.Ordinal.Equals(lease.HolderId, _holderId) &&
        StringComparer.Ordinal.Equals(lease.OwnerToken, _ownerToken);

    DateTimeOffset ObserveMonotonicUtcNow() =>
        new(ObserveMonotonicUtcTicks(), TimeSpan.Zero);

    long ObserveMonotonicUtcTicks()
    {
        var observed = _clock.UtcNow.UtcTicks;
        while (true)
        {
            var latest = Volatile.Read(ref _latestObservedUtcTicks);
            if (observed <= latest)
            {
                return latest;
            }

            if (Interlocked.CompareExchange(ref _latestObservedUtcTicks, observed, latest) == latest)
            {
                return observed;
            }
        }
    }

    void LoseLease()
    {
        if (Interlocked.Exchange(ref _lost, 1) == 0)
        {
            _leaseLossCallback?.Invoke();
        }
    }
}
