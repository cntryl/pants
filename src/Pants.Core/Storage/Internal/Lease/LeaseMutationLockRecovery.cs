using Cntryl.Pants.Storage.Internal;

namespace Cntryl.Pants.Storage.Internal.Lease;

/// <summary>
///     Explicit operator recovery for a <c>.midge_leader.lock</c> left behind by a writer that died
///     while holding it. The lock is never broken automatically (Midge's operator runbook: an
///     in-flight rename cannot be cancelled). This removes it only when the leader record proves
///     no writer is live and the lock still carries the owner token observed at the start.
/// </summary>
static class LeaseMutationLockRecovery
{
    const string LeaderFileName = ".midge_leader";
    const string LockFileName = ".midge_leader.lock";

    /// <returns><see langword="true" /> when a lock was removed; <see langword="false" /> when none existed.</returns>
    /// <exception cref="PantsLeaseHeldException">A writer is still live, or the lock changed during recovery.</exception>
    /// <exception cref="PantsLeaseIndeterminateException">
    ///     The lock has no owner token, or the leader record cannot be proven stale.
    /// </exception>
    public static bool Recover(string root, TimeSpan timeToLive, TimeSpan clockSkewTolerance, IPantsClock clock)
    {
        if (timeToLive <= TimeSpan.Zero || clockSkewTolerance < TimeSpan.Zero || clockSkewTolerance > timeToLive)
        {
            throw PantsException.InvalidArgument(
                "The file lease requires a positive TTL and non-negative clock skew that does not exceed that TTL.");
        }

        var lockPath = Path.Combine(root, LockFileName);
        var ownerToken = FileLease.TryReadOwnerToken(lockPath);
        if (ownerToken is null)
        {
            if (!File.Exists(lockPath))
            {
                return false;
            }

            throw new PantsLeaseIndeterminateException(
                "The Midge leader mutation lock has no owner token; its owner cannot be verified.");
        }

        RequireNoLiveWriter(Path.Combine(root, LeaderFileName), timeToLive, clockSkewTolerance, clock);

        // Re-check the token immediately before removal so a lock replaced since the check survives.
        if (FileLease.TryReadOwnerToken(lockPath) != ownerToken)
        {
            throw new PantsLeaseHeldException(
                "The Midge leader mutation lock changed during recovery; no lock was removed.");
        }

        File.Delete(lockPath);
        return true;
    }

    static void RequireNoLiveWriter(string leaderPath, TimeSpan timeToLive, TimeSpan clockSkewTolerance, IPantsClock clock)
    {
        var bytes = SharedReadFile.TryReadAllBytes(leaderPath);
        if (bytes is null)
        {
            return;
        }

        var record = LeaderRecordCodec.Decode(bytes);
        if (record is null)
        {
            return;
        }

        if (!Rfc3339Timestamp.TryParse(record.AcquiredAt, out var acquiredAt))
        {
            throw new PantsLeaseIndeterminateException(
                "Midge leader timestamp is invalid; ownership is ambiguous.");
        }

        var age = clock.UtcNow - acquiredAt;
        if (age < TimeSpan.Zero)
        {
            throw new PantsLeaseIndeterminateException(
                "Midge leader timestamp is in the future; ownership is ambiguous.");
        }

        // Same takeover boundary as FileLease: a record is stale once its age reaches TTL + skew.
        if (age < timeToLive + clockSkewTolerance)
        {
            throw new PantsLeaseHeldException(
                $"Another Midge-compatible writer '{record.HolderId}' may still be live; " +
                "its leader record has not expired, so the mutation lock was not removed.");
        }
    }
}
