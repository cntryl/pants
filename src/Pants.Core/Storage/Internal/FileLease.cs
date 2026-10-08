using System.Text;

namespace Cntryl.Pants.Storage.Internal;

sealed class FileLease : IDisposable
{
    const int ReleaseAttempts = 20;
    static readonly TimeSpan ReleaseRetryDelay = TimeSpan.FromMilliseconds(25);

    readonly IPantsClock _clock;
    readonly object _gate = new();
    readonly Timer _heartbeat;
    readonly string _holderId;
    readonly string _leaderPath;
    readonly Action? _leaseLossCallback;
    readonly string _lockPath;
    readonly TimeProvider _time;
    readonly long _timeToLiveTimestamps;
    readonly Timer _watchdog;
    long _validUntilTimestamp;
    bool _disposed;
    int _leaseLossNotified;
    volatile bool _valid = true;

    FileLease(
        string root,
        string holderId,
        ulong epoch,
        Action? leaseLossCallback,
        TimeSpan heartbeatInterval,
        IPantsClock clock,
        TimeProvider time,
        TimeSpan timeToLive,
        long acquireStartedTimestamp)
    {
        _leaderPath = Path.Combine(root, ".midge_leader");
        _lockPath = Path.Combine(root, ".midge_leader.lock");
        _holderId = holderId;
        _leaseLossCallback = leaseLossCallback;
        _clock = clock;
        _time = time;
        _timeToLiveTimestamps = ToTimestamps(time, timeToLive);
        _validUntilTimestamp = AddSaturating(acquireStartedTimestamp, _timeToLiveTimestamps);
        Epoch = epoch;
        _watchdog = new Timer(_ => OnWatchdog(), null, timeToLive, Timeout.InfiniteTimeSpan);
        _heartbeat = new Timer(_ => Renew(), null, heartbeatInterval, heartbeatInterval);
    }

    public ulong Epoch { get; }

    /// <summary>
    ///     Test-only hook invoked immediately after <see cref="Renew" /> writes the refreshed leader
    ///     record, before the write is re-verified. Lets tests simulate another writer racing in
    ///     during that window.
    /// </summary>
    internal Action? RenewWriteInterferenceHookForTesting { get; set; }

    /// <summary>
    ///     Test-only hook invoked when a <see cref="LeaseMutationLock" /> is disposed, after the
    ///     exclusive file handle is released but before the owner-token verification runs. Lets
    ///     tests simulate another writer replacing the lock file during that window.
    /// </summary>
    internal Action? MutationLockDisposalInterferenceHookForTesting { get; set; }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _heartbeat.Dispose();
            _watchdog.Dispose();
            TryWriteReleaseSentinel();
            _valid = false;
        }
    }

    /// <summary>
    ///     Runs once after this lease fences itself, outside any lock. The store uses it to drop
    ///     the same-host exclusion handle, which the lease spec allows only as defense in depth.
    /// </summary>
    internal Action? FencedRelease { get; set; }

    /// <summary>
    ///     Best effort: ages the record to the sentinel timestamp when it is still this lease's,
    ///     so a successor need not wait out the TTL. Never touches a record another writer owns.
    /// </summary>
    bool TryWriteReleaseSentinel()
    {
        try
        {
            using var leaseLock = AcquireMutationLock(
                _lockPath,
                _holderId,
                _clock,
                MutationLockDisposalInterferenceHookForTesting);
            var current = ReadRecord(_leaderPath);
            if (current?.Epoch == Epoch && current.HolderId == _holderId)
            {
                WriteRecord(_leaderPath, current with { AcquiredAt = "1970-01-01T00:00:00Z" });
            }

            return true;
        }
        catch
        {
            // Release is best-effort; the timestamp ages into a safe takeover.
            return false;
        }
    }

    void ReleaseAfterSelfFence()
    {
        // The mutation lock may be momentarily held by a successor probing the record, so a few
        // short retries keep the release from losing that race.
        for (var attempt = 0; attempt < ReleaseAttempts; attempt++)
        {
            lock (_gate)
            {
                if (_disposed || TryWriteReleaseSentinel())
                {
                    break;
                }
            }

            Thread.Sleep(ReleaseRetryDelay);
        }

        try
        {
            FencedRelease?.Invoke();
        }
        catch
        {
            // Dropping the exclusion handle is best-effort.
        }
    }

    public static FileLease Acquire(
        string root,
        ulong minimumEpoch,
        TimeSpan clockSkewTolerance,
        Action? leaseLossCallback,
        TimeSpan heartbeatInterval,
        IPantsClock? clock = null,
        TimeSpan? leaseTimeToLive = null,
        TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        var acquireStarted = time.GetTimestamp();
        var effectiveTimeToLive = leaseTimeToLive ?? TimeSpan.FromSeconds(30);
        if (effectiveTimeToLive < TimeSpan.FromMilliseconds(3) ||
            clockSkewTolerance < TimeSpan.Zero ||
            clockSkewTolerance >= effectiveTimeToLive)
        {
            throw PantsException.InvalidArgument(
                "The file lease requires a TTL of at least three milliseconds and " +
                "non-negative clock skew shorter than that TTL.");
        }

        var effectiveClock = clock ?? SystemPantsClock.Instance;
        var leaderPath = Path.Combine(root, ".midge_leader");
        var lockPath = Path.Combine(root, ".midge_leader.lock");
        var holderId = $"{Environment.ProcessId}.{Guid.NewGuid():N}@{Environment.MachineName}";
        using var leaseLock = AcquireMutationLock(lockPath, holderId, effectiveClock);
        var current = ReadRecord(leaderPath);
        if (current is not null)
        {
            if (!DateTimeOffset.TryParse(current.AcquiredAt, out var acquiredAt))
            {
                throw new PantsLeaseIndeterminateException(
                    "Midge leader timestamp is invalid; ownership is ambiguous.");
            }

            var age = effectiveClock.UtcNow - acquiredAt;
            if (age < TimeSpan.Zero)
            {
                throw new PantsLeaseIndeterminateException(
                    "Midge leader timestamp is in the future; ownership is ambiguous.");
            }

            var takeoverBoundary = AddSaturating(effectiveTimeToLive, clockSkewTolerance);
            if (age <= takeoverBoundary)
            {
                throw new PantsLeaseHeldException(
                    $"Another Midge-compatible writer '{current.HolderId}' owns this database; " +
                    $"configured LeaseTimeToLive is {effectiveTimeToLive:c} and " +
                    $"LeaseClockSkewTolerance is {clockSkewTolerance:c}.");
            }
        }

        var previousEpoch = Math.Max(current?.Epoch ?? 0, minimumEpoch);
        if (previousEpoch == ulong.MaxValue)
        {
            throw new PantsLeaseEpochExhaustedException(
                "The Midge writer lease epoch cannot be advanced.");
        }

        var epoch = previousEpoch + 1;
        WriteRecord(
            leaderPath,
            new LeaseRecord(epoch, holderId, effectiveClock.UtcNow.ToString("O")));
        var published = ReadRecord(leaderPath);
        if (published?.Epoch != epoch || published.HolderId != holderId)
        {
            throw new PantsLeaseHeldException("Lost the Midge leader publication race.");
        }

        return new FileLease(
            root,
            holderId,
            epoch,
            leaseLossCallback,
            heartbeatInterval,
            effectiveClock,
            time,
            effectiveTimeToLive,
            acquireStarted);
    }

    static long ToTimestamps(TimeProvider time, TimeSpan duration) =>
        duration.Ticks > long.MaxValue / Math.Max(1, time.TimestampFrequency / TimeSpan.TicksPerSecond + 1)
            ? long.MaxValue / 2
            : (long)(duration.TotalSeconds * time.TimestampFrequency);

    static long AddSaturating(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    bool IsPastDeadline() =>
        _time.GetTimestamp() >= Volatile.Read(ref _validUntilTimestamp);

    /// <summary>
    ///     Fences and reports loss once the monotonic deadline passes. It takes no lock, so a
    ///     renewal blocked in IO while holding the gate cannot delay it.
    /// </summary>
    void OnWatchdog()
    {
        var remaining = Volatile.Read(ref _validUntilTimestamp) - _time.GetTimestamp();
        if (remaining > 0)
        {
            RescheduleWatchdog(remaining);
            return;
        }

        if (_valid)
        {
            _valid = false;
        }

        NotifyLeaseLoss();
    }

    void RescheduleWatchdog(long remainingTimestamps)
    {
        try
        {
            var seconds = (double)remainingTimestamps / _time.TimestampFrequency;
            _watchdog.Change(
                TimeSpan.FromSeconds(Math.Max(seconds, 0.001)),
                Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // The lease was disposed; nothing is left to watch.
        }
    }

    /// <summary>Deterministically runs the expiry watchdog for testing.</summary>
    internal void CheckExpiryForTesting() => OnWatchdog();

    static TimeSpan AddSaturating(TimeSpan left, TimeSpan right) =>
        left.Ticks > TimeSpan.MaxValue.Ticks - right.Ticks
            ? TimeSpan.MaxValue
            : left + right;

    public void EnsureValid()
    {
        if (IsPastDeadline())
        {
            OnWatchdog();
            throw new PantsFencedException("The Midge writer lease is no longer valid.");
        }

        var leaseLost = false;
        lock (_gate)
        {
            if (!_valid || _disposed)
            {
                throw new PantsFencedException("The Midge writer lease is no longer valid.");
            }

            try
            {
                var current = ReadRecord(_leaderPath);
                if (current?.Epoch != Epoch || current.HolderId != _holderId)
                {
                    _valid = false;
                    leaseLost = true;
                }
            }
            catch (Exception exception) when (exception is PantsException or IOException)
            {
                _valid = false;
                leaseLost = true;
            }
        }

        if (leaseLost)
        {
            NotifyLeaseLoss();
            throw new PantsFencedException("The Midge writer lease is no longer valid.");
        }
    }

    /// <summary>Deterministically invokes the private renewal logic for testing.</summary>
    internal bool RenewForTesting()
    {
        Renew();
        return _valid;
    }

    void Renew()
    {
        var leaseLost = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var started = _time.GetTimestamp();
            if (started >= Volatile.Read(ref _validUntilTimestamp))
            {
                // A renewal that starts after the deadline cannot restore an expired lease.
                _valid = false;
                leaseLost = true;
            }
            else
            {
                try
                {
                    leaseLost = !TryRenewRecord() || _time.GetTimestamp() >=
                        Volatile.Read(ref _validUntilTimestamp);
                    if (leaseLost)
                    {
                        _valid = false;
                    }
                    else
                    {
                        // The deadline counts from when the renewal began, never from when it ended.
                        var deadline = AddSaturating(started, _timeToLiveTimestamps);
                        Volatile.Write(ref _validUntilTimestamp, deadline);
                        RescheduleWatchdog(deadline - _time.GetTimestamp());
                    }
                }
                catch
                {
                    _valid = false;
                    leaseLost = true;
                }
            }
        }

        if (leaseLost)
        {
            NotifyLeaseLoss();
        }
    }

    bool TryRenewRecord()
    {
        using var leaseLock = AcquireMutationLock(_lockPath, _holderId, _clock);
        var current = ReadRecord(_leaderPath);
        if (current?.Epoch != Epoch || current.HolderId != _holderId)
        {
            return false;
        }

        WriteRecord(
            _leaderPath,
            current with { AcquiredAt = _clock.UtcNow.ToString("O") });
        RenewWriteInterferenceHookForTesting?.Invoke();
        var published = ReadRecord(_leaderPath);
        return published?.Epoch == Epoch && published.HolderId == _holderId;
    }

    void NotifyLeaseLoss()
    {
        if (Interlocked.Exchange(ref _leaseLossNotified, 1) == 0)
        {
            try
            {
                _leaseLossCallback?.Invoke();
            }
            catch
            {
                // User callbacks cannot restore a lost lease or crash its heartbeat.
            }

            // A fenced writer must not keep a successor waiting. Done off this thread because it
            // takes the lease gate, which a blocked renewal may still hold.
            _ = Task.Run(ReleaseAfterSelfFence);
        }
    }

    static LeaseMutationLock AcquireMutationLock(
        string path,
        string holderId,
        IPantsClock clock,
        Action? disposalInterferenceHook = null)
    {
        try
        {
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var ownerToken = Guid.NewGuid().ToString("N");
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write(
                $"holder_id={holderId}\nowner_token={ownerToken}\ncreated_at={clock.UtcNow:O}\n");
            writer.Flush();
            stream.Flush(true);
            return new LeaseMutationLock(stream, path, ownerToken, disposalInterferenceHook);
        }
        catch (IOException ex)
        {
            throw new PantsLeaseUnavailableException(
                "Another Midge lease mutation is in progress.",
                ex);
        }
    }

    static string? TryReadOwnerToken(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "owner_token")
            {
                return parts[1];
            }
        }

        return null;
    }

    static LeaseRecord? ReadRecord(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split(": ", 2);
            if (parts.Length == 2)
            {
                // Last-occurrence-wins per format/lease.md §7: a duplicate field is resolved,
                // not treated as an indeterminate/corrupt record.
                fields[parts[0]] = parts[1];
            }
        }

        if (!(fields.TryGetValue("epoch", out var epochRaw) && ulong.TryParse(epochRaw, out var epoch) &&
              fields.TryGetValue("holder_id", out var holderId) &&
              fields.TryGetValue("acquired_at", out var acquiredAt)))
        {
            throw new PantsLeaseIndeterminateException(
                "Midge leader record is invalid; ownership is ambiguous.");
        }

        var record = new LeaseRecord(epoch, holderId, acquiredAt);

        // A record may carry a CRC32C over its three-field body. One that is present but wrong or
        // unparseable is a damaged record; one that is absent is an older, unchecked record.
        if (fields.TryGetValue("checksum", out var checksumRaw) &&
            (!uint.TryParse(checksumRaw, out var expected) || expected != Checksum(record)))
        {
            throw new PantsLeaseIndeterminateException(
                "Midge leader record checksum does not verify; ownership is ambiguous.");
        }

        return record;
    }

    static uint Checksum(LeaseRecord record) =>
        DiskFormat.Crc32C(Encoding.UTF8.GetBytes(Body(record)));

    static string Body(LeaseRecord record) =>
        $"epoch: {record.Epoch}\nholder_id: {record.HolderId}\nacquired_at: {record.AcquiredAt}\n";

    static void WriteRecord(string target, LeaseRecord record)
    {
        var content = $"{Body(record)}checksum: {Checksum(record)}\n";
        AtomicStagedFile.Write(target, Encoding.UTF8.GetBytes(content));
    }

    sealed record LeaseRecord(ulong Epoch, string HolderId, string AcquiredAt);

    sealed class LeaseMutationLock : IDisposable
    {
        readonly Action? _disposalInterferenceHook;
        readonly string _ownerToken;
        readonly string _path;
        readonly FileStream _stream;

        public LeaseMutationLock(
            FileStream stream,
            string path,
            string ownerToken,
            Action? disposalInterferenceHook)
        {
            _stream = stream;
            _path = path;
            _ownerToken = ownerToken;
            _disposalInterferenceHook = disposalInterferenceHook;
        }

        public void Dispose()
        {
            _stream.Dispose();
            _disposalInterferenceHook?.Invoke();
            try
            {
                // Only delete if this is still the same lock instance this process created,
                // checked via owner_token (format/lease.md §4 step 7). A mismatch means someone
                // else has since re-acquired the lock, so deleting would drop their lock.
                if (TryReadOwnerToken(_path) == _ownerToken)
                {
                    File.Delete(_path);
                }
            }
            catch
            {
            }
        }
    }
}
