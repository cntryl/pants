using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Cntryl.Pants.Runtime.Internal;

/// <summary>Coordinator-published hints and independent counter-only worker rate ownership.</summary>
sealed class WriteAdmission
{
    readonly PantsWriteBackpressureConfiguration _policy;
    readonly ConcurrentDictionary<ColumnFamilyIdentity, long> _slots = new(ColumnFamilyIdentityComparer.Instance);
    long _memoryBytes;
    ulong _pendingBytes;
    long _slowdownCount;
    long _slowdownNanoseconds;
    object? _manifestIdentity;
    int _familyCount = -1;
    ulong _nextFamilyId;

    public WriteAdmission(PantsWriteBackpressureConfiguration policy, TimeProvider timeProvider,
        IFailpointHandler failpoints)
    {
        _policy = policy;
        Limiter = policy.BackgroundWriteBytesPerSecond is { } rate
            ? new BackgroundWriteLimiter(rate, timeProvider, failpoints.BackgroundWriteRateWait)
            : null;
    }

    public bool Enabled => _policy.L0SoftLimit is not null || _policy.MemorySoftLimitBytes is not null ||
                           _policy.PendingCompactionSoftLimitBytes is not null;

    public bool TracksPending => _policy.PendingCompactionSoftLimitBytes is not null ||
                                 _policy.PendingCompactionHardLimitBytes is not null;

    public BackgroundWriteLimiter? Limiter { get; }
    public ulong PendingBytes => Volatile.Read(ref _pendingBytes);
    public bool HardPending => _policy.PendingCompactionHardLimitBytes is { } limit && PendingBytes >= limit;
    public long SlowdownCount => Volatile.Read(ref _slowdownCount);
    public long SlowdownNanoseconds => Volatile.Read(ref _slowdownNanoseconds);

    public TimeSpan Delay(IReadOnlyList<ColumnFamilyIdentity> families, bool queueSoft)
    {
        if (!Enabled || families.Count == 0)
        {
            return TimeSpan.Zero;
        }
        var soft = queueSoft ||
                   _policy.MemorySoftLimitBytes is { } memory && Volatile.Read(ref _memoryBytes) >= memory ||
                   _policy.PendingCompactionSoftLimitBytes is { } pending && PendingBytes >= pending ||
                   _policy.L0SoftLimit is { } l0 && families.Any(family => _slots.GetValueOrDefault(family) >= l0);
        return soft ? _policy.MaximumAdmissionDelay : TimeSpan.Zero;
    }

    public void RecordDelay(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _slowdownCount);
        Interlocked.Add(ref _slowdownNanoseconds,
            elapsed.Ticks > long.MaxValue / 100 ? long.MaxValue : elapsed.Ticks * 100);
    }

    public void Publish(RuntimePlan options, RuntimeState state,
        IReadOnlyDictionary<uint, ImmutableArray<FileMeta>> files, object? manifestIdentity)
    {
        if (Enabled)
        {
            foreach (var identity in _slots.Keys)
            {
                if (!state.FamilyData.ContainsKey(identity))
                {
                    _slots.TryRemove(identity, out _);
                }
            }
            foreach (var identity in state.FamilyData.Keys)
            {
                _slots[identity] = MemtableWritePressure.GetL0SlotUsage(state, identity);
            }
            Volatile.Write(ref _memoryBytes, MemtableWritePressure.GetTotalBytes(state));
        }
        if (TracksPending &&
            (!ReferenceEquals(_manifestIdentity, manifestIdentity) || _familyCount != state.FamilyData.Count ||
             _nextFamilyId != state.NextColumnFamilyId))
        {
            Volatile.Write(ref _pendingBytes, EstimatePendingBytes(options.Compaction, state, files));
            _manifestIdentity = manifestIdentity;
            _familyCount = state.FamilyData.Count;
            _nextFamilyId = state.NextColumnFamilyId;
        }
        Limiter?.SetUrgent(HardPending || MemtableWritePressure.IsStalled(options, state));
    }

    static ulong EstimatePendingBytes(PantsCompactionConfiguration policy, RuntimeState state,
        IReadOnlyDictionary<uint, ImmutableArray<FileMeta>> files)
    {
        ulong total = 0;
        foreach (var identity in state.FamilyData.Keys)
        {
            if (!files.TryGetValue(identity.Id, out var familyFiles))
            {
                continue;
            }
            foreach (var level in familyFiles.GroupBy(static file => file.Level))
            {
                if (level.Key >= policy.MaximumLevels - 1)
                {
                    continue;
                }
                var bytes = level.Aggregate(0UL, static (sum, file) => AddSaturating(sum, file.SizeBytes));
                ulong excess;
                if (level.Key == 0)
                {
                    excess = level.Count() >= policy.L0FileCountTrigger || bytes > (ulong)policy.L0SizeTriggerBytes
                        ? bytes
                        : 0;
                }
                else
                {
                    var target = LeveledCompactionPlanner.LevelTargetBytes(policy, level.Key);
                    excess = bytes > target ? bytes - target : 0;
                }
                total = AddSaturating(total, excess);
            }
        }
        return total;
    }

    static ulong AddSaturating(ulong first, ulong second) => ulong.MaxValue - first < second
        ? ulong.MaxValue
        : first + second;
}
