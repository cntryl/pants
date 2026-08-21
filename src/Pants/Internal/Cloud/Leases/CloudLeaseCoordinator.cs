namespace Pants;

internal sealed class CloudLeaseCoordinator : IDisposable
{
    private readonly ICloudLeaseStore _store;
    private readonly IPantsClock _clock;
    private readonly string _holderId;
    private readonly string _ownerToken = Guid.NewGuid().ToString("N");
    private readonly TimeSpan _leaseDuration;
    private readonly TimeSpan _clockSkewTolerance;
    private readonly Action? _leaseLossCallback;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ulong _epoch;
    private long _expiresAtUtcTicks;
    private int _lost;
    private int _disposed;

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

    public bool IsHealthy =>
        Volatile.Read(ref _lost) == 0 &&
        Epoch != 0 &&
        _clock.UtcNow.UtcTicks + _clockSkewTolerance.Ticks <
        Volatile.Read(ref _expiresAtUtcTicks);

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
            DateTimeOffset now = _clock.UtcNow;
            CloudLeaseSnapshot? current = await _store.ReadAsync(cancellationToken)
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
            CloudLeaseSnapshot? current = await _store.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (current is null ||
                current.Lease.Epoch != Epoch ||
                !StringComparer.Ordinal.Equals(current.Lease.HolderId, _holderId) ||
                !StringComparer.Ordinal.Equals(current.Lease.OwnerToken, _ownerToken))
            {
                LoseLease();
                throw new PantsFencedException("The cloud primary lease is owned by another writer.");
            }

            DateTimeOffset expiresAt = _clock.UtcNow + _leaseDuration;
            bool renewed = await _store.TryReplaceAsync(
                current.Version,
                current.Lease with { ExpiresAtUtc = expiresAt },
                cancellationToken).ConfigureAwait(false);
            if (!renewed)
            {
                LoseLease();
                throw new PantsFencedException("The cloud primary lease renewal was fenced.");
            }

            Volatile.Write(ref _expiresAtUtcTicks, expiresAt.UtcTicks);
        }
        catch (PantsLeaseIndeterminateException)
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Volatile.Write(ref _lost, 1);
            _gate.Dispose();
        }
    }

    private void LoseLease()
    {
        if (Interlocked.Exchange(ref _lost, 1) == 0)
        {
            _leaseLossCallback?.Invoke();
        }
    }
}
