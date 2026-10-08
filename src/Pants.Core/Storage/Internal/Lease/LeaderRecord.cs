namespace Cntryl.Pants.Storage.Internal.Lease;

/// <summary>The three fields of a local <c>.midge_leader</c> record (format/lease.md §3).</summary>
/// <remarks>
///     <see cref="AcquiredAt" /> stays the raw text: only a takeover decision needs it as an
///     instant, and that decision parses it strictly through <see cref="Rfc3339Timestamp" />.
/// </remarks>
sealed record LeaderRecord(ulong Epoch, string HolderId, string AcquiredAt);
