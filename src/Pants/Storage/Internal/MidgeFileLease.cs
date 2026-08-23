using System.Text;

namespace Cntryl.Pants.Storage.Internal;

sealed class MidgeFileLease : IDisposable
{
    static readonly TimeSpan LeaseTakeoverBaseDelay = TimeSpan.FromSeconds(60);
    readonly object _gate = new();
    readonly Timer _heartbeat;
    readonly string _holderId;
    readonly string _leaderPath;
    readonly Action? _leaseLossCallback;
    readonly string _lockPath;
    bool _disposed;
    int _leaseLossNotified;
    volatile bool _valid = true;

    MidgeFileLease(
        string root,
        string holderId,
        ulong epoch,
        Action? leaseLossCallback,
        TimeSpan heartbeatInterval)
    {
        _leaderPath = Path.Combine(root, ".midge_leader");
        _lockPath = Path.Combine(root, ".midge_leader.lock");
        _holderId = holderId;
        _leaseLossCallback = leaseLossCallback;
        Epoch = epoch;
        _heartbeat = new Timer(_ => Renew(), null, heartbeatInterval, heartbeatInterval);
    }

    public ulong Epoch { get; }

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
                using var leaseLock = AcquireMutationLock(_lockPath, _holderId);
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

    public static MidgeFileLease Acquire(
        string root,
        ulong minimumEpoch,
        TimeSpan clockSkewTolerance,
        Action? leaseLossCallback,
        TimeSpan heartbeatInterval)
    {
        var leaderPath = Path.Combine(root, ".midge_leader");
        var lockPath = Path.Combine(root, ".midge_leader.lock");
        var holderId = $"{Environment.ProcessId}.{Guid.NewGuid():N}@{Environment.MachineName}";
        using var leaseLock = AcquireMutationLock(lockPath, holderId);
        var current = ReadRecord(leaderPath);
        if (current is not null)
        {
            if (!DateTimeOffset.TryParse(current.AcquiredAt, out var acquiredAt))
            {
                throw new PantsLeaseIndeterminateException(
                    "Midge leader timestamp is invalid; ownership is ambiguous.");
            }

            var age = DateTimeOffset.UtcNow - acquiredAt;
            if (age < TimeSpan.Zero)
            {
                throw new PantsLeaseIndeterminateException(
                    "Midge leader timestamp is in the future; ownership is ambiguous.");
            }

            if (age < LeaseTakeoverBaseDelay + clockSkewTolerance)
            {
                throw new PantsLeaseHeldException(
                    $"Another Midge-compatible writer '{current.HolderId}' owns this database.");
            }
        }

        var previousEpoch = Math.Max(current?.Epoch ?? 0, minimumEpoch);
        if (previousEpoch == ulong.MaxValue)
        {
            throw new PantsLeaseEpochExhaustedException(
                "The Midge writer lease epoch cannot be advanced.");
        }

        var epoch = previousEpoch + 1;
        WriteRecord(leaderPath, new LeaseRecord(epoch, holderId, DateTimeOffset.UtcNow.ToString("O")));
        var published = ReadRecord(leaderPath);
        if (published?.Epoch != epoch || published.HolderId != holderId)
        {
            throw new PantsLeaseHeldException("Lost the Midge leader publication race.");
        }

        return new MidgeFileLease(root, holderId, epoch, leaseLossCallback, heartbeatInterval);
    }

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
                using var leaseLock = AcquireMutationLock(_lockPath, _holderId);
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
                        current with { AcquiredAt = DateTimeOffset.UtcNow.ToString("O") });
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

    static LeaseMutationLock AcquireMutationLock(string path, string holderId)
    {
        try
        {
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write(
                $"holder_id={holderId}\nowner_token={Guid.NewGuid():N}\ncreated_at={DateTimeOffset.UtcNow:O}\n");
            writer.Flush();
            stream.Flush(true);
            return new LeaseMutationLock(stream, path);
        }
        catch (IOException ex)
        {
            throw new PantsLeaseUnavailableException(
                "Another Midge lease mutation is in progress.",
                ex);
        }
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
            if (parts.Length == 2 && !fields.TryAdd(parts[0], parts[1]))
            {
                throw new PantsLeaseIndeterminateException(
                    $"Midge leader record contains duplicate field '{parts[0]}'.");
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
        readonly string _path;
        readonly FileStream _stream;

        public LeaseMutationLock(FileStream stream, string path)
        {
            _stream = stream;
            _path = path;
        }

        public void Dispose()
        {
            _stream.Dispose();
            try
            {
                File.Delete(_path);
            }
            catch
            {
            }
        }
    }
}
