namespace Pants;

internal sealed class MidgeFileLease : IDisposable
{
    private static readonly TimeSpan LeaseTakeoverBaseDelay = TimeSpan.FromSeconds(60);
    private readonly string _leaderPath;
    private readonly string _lockPath;
    private readonly string _holderId;
    private readonly Action? _leaseLossCallback;
    private readonly object _gate = new();
    private readonly Timer _heartbeat;
    private volatile bool _valid = true;
    private bool _disposed;
    private int _leaseLossNotified;

    private MidgeFileLease(
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

        ulong previousEpoch = Math.Max(current?.Epoch ?? 0, minimumEpoch);
        if (previousEpoch == ulong.MaxValue)
        {
            throw new PantsLeaseEpochExhaustedException(
                "The Midge writer lease epoch cannot be advanced.");
        }

        ulong epoch = previousEpoch + 1;
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
        if (!_valid || _disposed)
        {
            throw new PantsFencedException("The Midge writer lease is no longer valid.");
        }
    }

    private void Renew()
    {
        bool leaseLost = false;
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

        if (leaseLost && Interlocked.Exchange(ref _leaseLossNotified, 1) == 0)
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

    private static LeaseMutationLock AcquireMutationLock(string path, string holderId)
    {
        try
        {
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write($"holder_id={holderId}\nowner_token={Guid.NewGuid():N}\ncreated_at={DateTimeOffset.UtcNow:O}\n");
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return new LeaseMutationLock(stream, path);
        }
        catch (IOException ex)
        {
            throw new PantsLeaseUnavailableException(
                "Another Midge lease mutation is in progress.",
                ex);
        }
    }

    private static LeaseRecord? ReadRecord(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var fields = File.ReadAllLines(path)
            .Select(line => line.Split(": ", 2, StringSplitOptions.None))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        return fields.TryGetValue("epoch", out var epochRaw) && ulong.TryParse(epochRaw, out var epoch) &&
               fields.TryGetValue("holder_id", out var holderId) && fields.TryGetValue("acquired_at", out var acquiredAt)
            ? new LeaseRecord(epoch, holderId, acquiredAt)
            : throw new PantsLeaseIndeterminateException(
                "Midge leader record is invalid; ownership is ambiguous.");
    }

    private static void WriteRecord(string target, LeaseRecord record)
    {
        var content = $"epoch: {record.Epoch}\nholder_id: {record.HolderId}\nacquired_at: {record.AcquiredAt}\n";
        AtomicStagedFile.Write(target, System.Text.Encoding.UTF8.GetBytes(content));
    }

    private sealed record LeaseRecord(ulong Epoch, string HolderId, string AcquiredAt);

    private sealed class LeaseMutationLock : IDisposable
    {
        private readonly FileStream _stream;
        private readonly string _path;

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
