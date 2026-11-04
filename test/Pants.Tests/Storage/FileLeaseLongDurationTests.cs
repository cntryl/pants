using System.Collections.Concurrent;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class FileLeaseLongDurationTests
{
    [Fact]
    public async Task ShouldOpenCommitAndReopenWithAValidYearLongLeaseUsingSystemTime()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)
            .WithLeaseTimeToLive(TimeSpan.FromDays(365));
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await using var writer = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            writer.Put("year-long"u8.ToArray(), "acknowledged"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.Sync);
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var read = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("acknowledged"u8.ToArray(), (await read.GetAsync("year-long"u8.ToArray()))?.ToArray());
    }

    [Fact]
    public void ShouldChunkLongHeartbeatAndWatchdogTimersWithoutRenewingEarly()
    {
        using var directory = new TemporaryDirectory();
        var wall = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var time = new BoundedSchedulingTimeProvider(wall);
        var ttl = TimeSpan.FromDays(365);
        var interval = ttl / 3;
        var renewals = 0;
        var losses = 0;
        using var lease = FileLease.Acquire(directory.Path, 0, ttl / 2,
            () => losses++, interval, wall, ttl, time);
        lease.RenewWriteInterferenceHookForTesting = () => renewals++;
        time.Advance(interval - TimeSpan.FromTicks(1));
        Assert.Equal(0, renewals);
        Assert.True(lease.IsKnownValid);
        time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(1, renewals);
        time.Advance(interval - TimeSpan.FromTicks(1));
        Assert.Equal(1, renewals);
        time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(2, renewals);
        time.Advance(interval);
        Assert.Equal(3, renewals);
        Assert.True(lease.IsKnownValid);
        Assert.Equal(0, losses);
        Assert.True(time.Requests.Count > 365);
        lease.Dispose();
        Assert.Equal(0, time.ActiveTimers);
        time.Advance(ttl);
        Assert.Equal(3, renewals);
        Assert.Equal(0, losses);
    }

    [Fact]
    public async Task ShouldRearmAChunkedWatchdogUntilTheActualLongValidityExpires()
    {
        using var directory = new TemporaryDirectory();
        var wall = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var time = new BoundedSchedulingTimeProvider(wall);
        var ttl = TimeSpan.FromDays(365);
        var losses = 0;
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lease = FileLease.Acquire(directory.Path, 0, ttl / 2,
            () => Interlocked.Increment(ref losses), ttl + ttl, wall, ttl, time);
        lease.FencedRelease = () => released.TrySetResult();
        time.Advance(ttl - TimeSpan.FromTicks(1));
        Assert.True(lease.IsKnownValid);
        Assert.Equal(0, Volatile.Read(ref losses));
        time.Advance(TimeSpan.FromTicks(1));
        await released.Task.WaitAsync(TestTimeouts.Expected);
        Assert.False(lease.IsKnownValid);
        Assert.Equal(1, Volatile.Read(ref losses));
        Assert.Throws<PantsFencedException>(lease.EnsureValid);
        lease.CheckExpiryForTesting();
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, Volatile.Read(ref losses));
        lease.Dispose();
        Assert.Equal(0, time.ActiveTimers);
    }

    [Fact]
    public void ShouldNotFenceAdvancedValidityWhenAWatchdogResumesWithItsOldSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var wall = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var time = new BoundedSchedulingTimeProvider(wall);
        var losses = 0;
        using var lease = FileLease.Acquire(directory.Path, 0, TimeSpan.Zero, () => losses++, TimeSpan.FromHours(1),
            wall, TimeSpan.FromSeconds(30), time);
        time.Advance(TimeSpan.FromSeconds(20));
        time.BeforeNextTimestampRead = () =>
        {
            Assert.True(lease.RenewForTesting());
            time.Advance(TimeSpan.FromSeconds(10));
        };

        lease.CheckExpiryForTesting();

        Assert.True(lease.IsKnownValid);
        Assert.Equal(0, losses);
        lease.EnsureValid();
    }

    [Fact]
    public void ShouldScheduleTheNextHeartbeatFromTheStartOfALateSuccessfulRenewal()
    {
        using var directory = new TemporaryDirectory();
        var wall = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var time = new BoundedSchedulingTimeProvider(wall);
        var renewals = 0;
        using var lease = FileLease.Acquire(directory.Path, 0, TimeSpan.Zero, null, TimeSpan.FromSeconds(10),
            wall, TimeSpan.FromSeconds(30), time);
        lease.RenewWriteInterferenceHookForTesting = () => renewals++;
        var heartbeat = time.HoldNextInitiallyDue(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(heartbeat.HasDeferredCallback);
        time.Advance(TimeSpan.FromSeconds(5));
        heartbeat.ReleaseDeferredCallback();
        Assert.Equal(1, renewals);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, renewals);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, renewals);
        Assert.True(lease.IsKnownValid);
    }

    [Fact]
    public void ShouldIgnoreAnAlreadyDispatchedWatchdogAfterIntentionalDisposal()
    {
        using var directory = new TemporaryDirectory();
        var wall = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var time = new BoundedSchedulingTimeProvider(wall);
        var losses = 0;
        using var lease = FileLease.Acquire(directory.Path, 0, TimeSpan.Zero, () => losses++, TimeSpan.FromHours(1),
            wall, TimeSpan.FromSeconds(30), time);
        var watchdog = time.HoldNextInitiallyDue(TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(watchdog.HasDeferredCallback);
        lease.Dispose();
        using var successor = FileLease.Acquire(directory.Path, 0, TimeSpan.Zero, null, TimeSpan.FromHours(1),
            wall, TimeSpan.FromSeconds(30), time);
        var record = File.ReadAllBytes(Path.Combine(directory.Path, ".midge_leader"));

        watchdog.ReleaseDeferredCallback();

        Assert.Equal(0, losses);
        Assert.Equal(record, File.ReadAllBytes(Path.Combine(directory.Path, ".midge_leader")));
        successor.EnsureValid();
    }

    [Fact]
    public async Task ShouldRetainNativeGuardReleaseUntilAnExpiredPhysicalRenewalLeavesItsIoGate()
    {
        using var directory = new TemporaryDirectory();
        var wall = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var time = new BoundedSchedulingTimeProvider(wall);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowReadback = new ManualResetEventSlim(false);
        var losses = 0;
        using var lease = FileLease.Acquire(directory.Path, 0, TimeSpan.Zero,
            () => Interlocked.Increment(ref losses), TimeSpan.FromHours(1), wall, TimeSpan.FromSeconds(30), time);
        lease.FencedRelease = () => released.TrySetResult();
        lease.RenewWriteInterferenceHookForTesting = () =>
        {
            entered.TrySetResult();
            if (!allowReadback.Wait(TestTimeouts.Expected)) { throw new TimeoutException("The held renewal was not released."); }
        };
        time.Advance(TimeSpan.FromSeconds(20));
        var renewal = Task.Run(lease.RenewForTesting);
        try
        {
            await entered.Task.WaitAsync(TestTimeouts.Expected);
            await Task.Run(() => time.Advance(TimeSpan.FromSeconds(10))).WaitAsync(TestTimeouts.Expected);
            Assert.Equal(1, Volatile.Read(ref losses));
            Assert.False(renewal.IsCompleted);
            Assert.False(released.Task.IsCompleted);
        }
        finally
        {
            allowReadback.Set();
        }
        Assert.False(await renewal.WaitAsync(TestTimeouts.Expected));
        await released.Task.WaitAsync(TestTimeouts.Expected);
        using var successor = FileLease.Acquire(directory.Path, 0, TimeSpan.Zero, null, TimeSpan.FromHours(1),
            wall, TimeSpan.FromSeconds(30), time);
        Assert.Equal(lease.Epoch + 1, successor.Epoch);
        successor.EnsureValid();
        Assert.Equal(1, Volatile.Read(ref losses));
    }

    sealed class BoundedSchedulingTimeProvider(ManualClock wall) : TimeProvider
    {
        static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromDays(1);
        readonly ManualSchedulingTimeProvider _inner = new();
        readonly List<ObservedTimer> _timers = [];
        Action? _beforeTimestampRead;
        public ConcurrentQueue<(TimeSpan Due, TimeSpan Period)> Requests { get; } = new();
        public int ActiveTimers => _timers.Count(static timer => !timer.Disposed);
        public Action? BeforeNextTimestampRead { set => Interlocked.Exchange(ref _beforeTimestampRead, value); }
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override long GetTimestamp()
        {
            Interlocked.Exchange(ref _beforeTimestampRead, null)?.Invoke();
            return _inner.GetTimestamp();
        }
        public void Advance(TimeSpan duration)
        {
            wall.UtcNow += duration;
            _inner.Advance(duration);
        }
        public ObservedTimer HoldNextInitiallyDue(TimeSpan due)
        {
            var timer = _timers.Single(timer => timer.InitialDue == due);
            timer.HoldNextCallback = true;
            return timer;
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Record(dueTime, period);
            var timer = new ObservedTimer(this, callback, state, dueTime);
            timer.Inner = _inner.CreateTimer(_ => timer.Fire(), null, dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        void Record(TimeSpan due, TimeSpan period)
        {
            Requests.Enqueue((due, period));
            if (due != Timeout.InfiniteTimeSpan && (due < TimeSpan.Zero || due > MaximumTimerDelay))
            {
                throw new ArgumentOutOfRangeException(nameof(due), "The test clock rejects uncapped timer due times.");
            }
            if (period != Timeout.InfiniteTimeSpan && (period < TimeSpan.Zero || period > MaximumTimerDelay))
            {
                throw new ArgumentOutOfRangeException(nameof(period), "The test clock rejects uncapped periodic timers.");
            }
        }
        public sealed class ObservedTimer(BoundedSchedulingTimeProvider owner, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            Action? _deferred;
            int _disposed;
            public ITimer Inner { get; set; } = null!;
            public TimeSpan? InitialDue { get; private set; } = due == Timeout.InfiniteTimeSpan ? null : due;
            public bool Disposed => Volatile.Read(ref _disposed) != 0;
            public bool HoldNextCallback { get; set; }
            public bool HasDeferredCallback => _deferred is not null;
            public void Fire()
            {
                if (HoldNextCallback)
                {
                    HoldNextCallback = false;
                    _deferred = () => callback(state);
                }
                else { callback(state); }
            }
            public void ReleaseDeferredCallback() => Interlocked.Exchange(ref _deferred, null)!.Invoke();
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                owner.Record(dueTime, period);
                if (dueTime != Timeout.InfiniteTimeSpan) { InitialDue ??= dueTime; }
                return Inner.Change(dueTime, period);
            }
            public void Dispose()
            {
                Interlocked.Exchange(ref _disposed, 1);
                Inner.Dispose();
            }
            public ValueTask DisposeAsync()
            {
                Interlocked.Exchange(ref _disposed, 1);
                return Inner.DisposeAsync();
            }
        }
    }
}
