using Cntryl.Pants.Exceptions;
using Cntryl.Pants.Observability;
using Cntryl.Pants.Storage.Internal.Accounting;

namespace Cntryl.Pants.Checkpoint;

static class CheckpointBoundary
{
    public static readonly TimeSpan MaximumBudget = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PauseSlice = TimeSpan.FromMilliseconds(1);

    public static async ValueTask<Capture> CaptureAsync(
        TimeProvider time, long cellDeadline, TimeSpan budget, Observation observation,
        Func<TimeSpan, ValueTask<(PantsRuntimeMetrics Runtime, MetadataAccountingSnapshot Snapshot)>> query,
        Func<TimeSpan, ValueTask>? pause = null)
    {
        var started = time.GetTimestamp();
        var deadline = Math.Min(cellDeadline, CheckpointClock.Deadline(time, started,
            budget < MaximumBudget ? budget : MaximumBudget));
        pause ??= delay => new ValueTask(Task.Delay(delay, time));
        while (true)
        {
            var queryStarted = time.GetTimestamp();
            var remaining = CheckpointClock.Remaining(time, deadline);
            observation.ElapsedNanoseconds = CheckpointClock.Nanoseconds(time.GetElapsedTime(started));
            if (remaining <= TimeSpan.Zero)
            {
                throw Expired();
            }
            observation.Samples++;
            var candidate = await query(remaining).ConfigureAwait(false);
            var captured = time.GetTimestamp();
            observation.ElapsedNanoseconds = CheckpointClock.Nanoseconds(time.GetElapsedTime(started, captured));
            var active = candidate.Snapshot.Buckets.Where(static bucket => bucket.Medium == MetadataMedium.Persistent)
                .Aggregate((UInt128)0, static (total, bucket) => total + bucket.ActiveOperations);
            observation.FinalPersistentActive = checked((ulong)active);
            if (active > 0)
            {
                observation.ActiveSamples++;
            }
            if (captured >= deadline)
            {
                throw Expired();
            }
            if (active == 0)
            {
                observation.Complete = true;
                return new Capture(candidate.Runtime, candidate.Snapshot, queryStarted);
            }
            remaining = CheckpointClock.Remaining(time, deadline);
            if (remaining <= TimeSpan.Zero)
            {
                throw Expired();
            }
            var allowance = remaining < PauseSlice ? remaining : PauseSlice;
            observation.PauseCalls++;
            observation.PauseRequestedNanoseconds += CheckpointClock.Nanoseconds(allowance);
            observation.MaximumPauseRequestedNanoseconds = Math.Max(observation.MaximumPauseRequestedNanoseconds,
                CheckpointClock.Nanoseconds(allowance));
            var pauseStarted = time.GetTimestamp();
            await pause(allowance).ConfigureAwait(false);
            observation.PausedNanoseconds += CheckpointClock.Nanoseconds(time.GetElapsedTime(pauseStarted));
        }
    }

    static PantsTimeoutException Expired() => new("Metadata boundary allowance expired before an idle Persistent capture.");

    public sealed record Capture(PantsRuntimeMetrics Runtime, MetadataAccountingSnapshot Snapshot, long QueryStarted);

    public sealed class Observation
    {
        public ulong Samples { get; set; }
        public ulong ActiveSamples { get; set; }
        public ulong PauseCalls { get; set; }
        public ulong PauseRequestedNanoseconds { get; set; }
        public ulong MaximumPauseRequestedNanoseconds { get; set; }
        public ulong PausedNanoseconds { get; set; }
        public ulong ElapsedNanoseconds { get; set; }
        public ulong FinalPersistentActive { get; set; }
        public bool Complete { get; set; }
        public bool ProgressAdvanced { get; init; }
    }
}
