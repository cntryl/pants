using System.Text;

namespace Cntryl.Pants.Storage.Internal;

sealed class FileLease : IDisposable
{
    readonly IPantsClock _clock;
    readonly object _gate = new();
    readonly Timer _heartbeat;
    readonly string _holderId;
    readonly string _leaderPath;
    readonly Action? _leaseLossCallback;
    readonly string _lockPath;
    bool _disposed;
    int _leaseLossNotified;
    volatile bool _valid = true;

    FileLease(
        string root,
        string holderId,
        ulong epoch,
        Action? leaseLossCallback,
        TimeSpan heartbeatInterval,
        IPantsClock clock)
    {
        _leaderPath = Path.Combine(root, ".midge_leader");
        _lockPath = Path.Combine(root, ".midge_leader.lock");
        _holderId = holderId;
        _leaseLossCallback = leaseLossCallback;
        _clock = clock;
        Epoch = epoch;
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
            }
            catch
            {
                // Disposal is best-effort; the timestamp ages into a safe takeover.
            }

            _valid = false;
        }
    }

    public static FileLease Acquire(
        string root,
        ulong minimumEpoch,
        TimeSpan clockSkewTolerance,
        Action? leaseLossCallback,
        TimeSpan heartbeatInterval,
        IPantsClock? clock = null,
        TimeSpan? leaseTimeToLive = null)
    {
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
            effectiveClock);
    }

    static TimeSpan AddSaturating(TimeSpan left, TimeSpan right) =>
        left.Ticks > TimeSpan.MaxValue.Ticks - right.Ticks
            ? TimeSpan.MaxValue
            : left + right;

    public void EnsureValid()
    {
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

            try
            {
                using var leaseLock = AcquireMutationLock(_lockPath, _holderId, _clock);
                var current = ReadRecord(_leaderPath);
                if (current?.Epoch != Epoch || current.HolderId != _holderId)
                {
                    _valid = false;
                    leaseLost = true;
                }
                else
                {
                    WriteRecord(
                        _leaderPath,
                        current with { AcquiredAt = _clock.UtcNow.ToString("O") });
                    RenewWriteInterferenceHookForTesting?.Invoke();
                    var published = ReadRecord(_leaderPath);
                    if (published?.Epoch != Epoch || published.HolderId != _holderId)
                    {
                        _valid = false;
                        leaseLost = true;
                    }
                }
            }
            catch
            {
                _valid = false;
                leaseLost = true;
            }
        }

        if (leaseLost)
        {
            NotifyLeaseLoss();
        }
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

        return fields.TryGetValue("epoch", out var epochRaw) && ulong.TryParse(epochRaw, out var epoch) &&
               fields.TryGetValue("holder_id", out var holderId) &&
               fields.TryGetValue("acquired_at", out var acquiredAt)
            ? new LeaseRecord(epoch, holderId, acquiredAt)
            : throw new PantsLeaseIndeterminateException(
                "Midge leader record is invalid; ownership is ambiguous.");
    }

    static void WriteRecord(string target, LeaseRecord record)
    {
        var content = $"epoch: {record.Epoch}\nholder_id: {record.HolderId}\nacquired_at: {record.AcquiredAt}\n";
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
