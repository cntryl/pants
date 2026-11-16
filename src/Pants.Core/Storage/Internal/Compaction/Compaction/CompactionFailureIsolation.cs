using System.Runtime.ExceptionServices;

namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

/// <summary>
///     Lets a multi-turn compaction drain keep serving healthy column families after one family's
///     turn fails, then surface the first failure once nothing else can make progress. Only
///     failures attributable to the family's own data or files are isolated; cancellation, fencing,
///     lease loss, disposal and internal faults always end the drain immediately.
/// </summary>
sealed class CompactionFailureIsolation
{
    readonly HashSet<uint> _failedFamilies = [];
    ExceptionDispatchInfo? _firstFailure;

    public IReadOnlySet<uint> FailedFamilies => _failedFamilies;

    public bool TryIsolate(CompactionWorkSnapshot snapshot, Exception exception)
    {
        if (exception is not (PantsCorruptionException or PantsWriteConflictException or
            PantsIOException or IOException) ||
            snapshot.Plans.Count == 0)
        {
            return false;
        }

        foreach (var plan in snapshot.Plans)
        {
            _failedFamilies.Add(plan.ColumnFamilyId);
        }

        _firstFailure ??= ExceptionDispatchInfo.Capture(exception);
        return true;
    }

    public void ThrowIfFailed() => _firstFailure?.Throw();
}
