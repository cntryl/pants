using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Storage.Internal.IO;

namespace Cntryl.Pants.Storage.Internal.Lease;

/// <summary>
///     Explicit operator recovery for a <c>.midge_leader.lock</c> left behind by a writer that died
///     while holding it. Ordinary acquisition never breaks the lock. Recovery removes it only when
///     the leader record proves no writer is live and no process still holds the marker; when an
///     owner token is present, it must still match the token observed at the start.
/// </summary>
static class LeaseMutationLockRecovery
{
    const string LeaderFileName = ".midge_leader";
    const string LockFileName = ".midge_leader.lock";

    /// <returns><see langword="true" /> when a lock was removed; <see langword="false" /> when none existed.</returns>
    /// <exception cref="PantsLeaseHeldException">A writer is still live, or the lock changed during recovery.</exception>
    /// <exception cref="PantsLeaseIndeterminateException">The leader record cannot be proven stale.</exception>
    public static bool Recover(string root, TimeSpan timeToLive, TimeSpan clockSkewTolerance, IPantsClock clock)
    {
        if (timeToLive <= TimeSpan.Zero || clockSkewTolerance < TimeSpan.Zero || clockSkewTolerance > timeToLive)
        {
            throw PantsException.InvalidArgument(
                "The file lease requires a positive TTL and non-negative clock skew that does not exceed that TTL.");
        }

        root = StoragePathGuard.CanonicalizeRoot(root);
        using var rootTrust = StoragePathGuard.TrustRoot(root);
        var lockPath = Path.Combine(root, LockFileName);
        if (!File.Exists(lockPath))
        {
            return false;
        }

        var ownerToken = ReadOwnerTokenOrRefuse(lockPath);
        if (!File.Exists(lockPath))
        {
            throw new PantsLeaseHeldException(
                "The Midge leader mutation lock changed during recovery; no lock was removed.");
        }

        RequireNoLiveWriter(Path.Combine(root, LeaderFileName), timeToLive, clockSkewTolerance, clock);

        // Re-check the token immediately before removal so a lock replaced since the check survives.
        // A tokenless marker is recoverable only after the stale leader check above; a live holder
        // cannot be read because its exclusive OS handle causes ReadOwnerTokenOrRefuse to refuse.
        if (!File.Exists(lockPath) || ReadOwnerTokenOrRefuse(lockPath) != ownerToken)
        {
            throw new PantsLeaseHeldException(
                "The Midge leader mutation lock changed during recovery; no lock was removed.");
        }

        StoragePathGuard.EnsureNotLink(lockPath);
        File.Delete(lockPath);
        return true;
    }

    /// <summary>
    ///     A sharing violation means another process still holds the lock open, so its owner is
    ///     alive: refuse with <see cref="PantsLeaseHeldException" /> rather than leak an I/O error.
    /// </summary>
    static string? ReadOwnerTokenOrRefuse(string lockPath)
    {
        try
        {
            return FileLease.TryReadOwnerToken(lockPath);
        }
        catch (IOException exception)
        {
            throw new PantsLeaseHeldException(
                "The Midge leader mutation lock is held by another process; no lock was removed.",
                exception);
        }
    }

    static void RequireNoLiveWriter(string leaderPath, TimeSpan timeToLive, TimeSpan clockSkewTolerance, IPantsClock clock)
    {
        StoragePathGuard.EnsureNotLink(leaderPath);
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
