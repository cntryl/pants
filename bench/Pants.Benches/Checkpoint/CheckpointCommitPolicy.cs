using Cntryl.Pants.Exceptions;

namespace Cntryl.Pants.Checkpoint;

static class CheckpointCommitPolicy
{
    public static async ValueTask CommitAsync(TimeProvider time, long cellDeadline, Observation observation,
        Func<ValueTask> commit, Func<TimeSpan, ValueTask<bool>> wait)
    {
        var deadline = Math.Min(cellDeadline, CheckpointClock.Deadline(time, time.GetTimestamp(), TimeSpan.FromSeconds(30)));
        while (true)
        {
            if (CheckpointClock.Remaining(time, deadline) <= TimeSpan.Zero)
            {
                throw Expired(observation);
            }
            observation.Attempts++;
            try
            {
                await commit().ConfigureAwait(false);
                observation.SuccessfulCommits++;
                return;
            }
            catch (PantsWriteStallException exception)
            {
                observation.WriteStalls++;
                observation.LastStall = exception.Message;
            }
            while (true)
            {
                var remaining = CheckpointClock.Remaining(time, deadline);
                if (remaining <= TimeSpan.Zero)
                {
                    throw Expired(observation);
                }
                var allowance = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
                observation.WaitCalls++;
                var started = time.GetTimestamp();
                try
                {
                    if (await wait(allowance).ConfigureAwait(false))
                    {
                        break;
                    }
                    observation.WaitTimeouts++;
                }
                catch (PantsWriteStallException exception)
                {
                    observation.WaitWriteStalls++;
                    observation.LastStall = exception.Message;
                }
                finally
                {
                    observation.WaitElapsedNanoseconds += CheckpointClock.Nanoseconds(time.GetElapsedTime(started));
                }
            }
        }
    }

    static PantsTimeoutException Expired(Observation observation) =>
        new($"Checkpoint commit allowance expired; last rejected stall: {observation.LastStall ?? "none"}.");

    public sealed class Observation
    {
        public ulong Attempts { get; set; }
        public ulong SuccessfulCommits { get; set; }
        public ulong WriteStalls { get; set; }
        public ulong WaitCalls { get; set; }
        public ulong WaitTimeouts { get; set; }
        public ulong WaitWriteStalls { get; set; }
        public ulong WaitElapsedNanoseconds { get; set; }
        public string? LastStall { get; set; }
    }
}
