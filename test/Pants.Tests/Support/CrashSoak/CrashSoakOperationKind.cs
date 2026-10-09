namespace Cntryl.Pants.Support.CrashSoak;

enum CrashSoakOperationKind
{
    /// <summary>A read-write transaction committed with the operation's durability.</summary>
    Commit,

    /// <summary>A transaction whose mutations are staged and then rolled back.</summary>
    Rollback,

    /// <summary>A transaction carrying a value assertion that can never hold, so its commit must fail.</summary>
    AssertionFailure,

    /// <summary>The committed side of a write-write conflict.</summary>
    ConflictWinner,

    /// <summary>
    ///     An <see cref="PantsConflictPolicy.AbortOnWriteConflict" /> transaction begun before its
    ///     winner committed an overlapping key, so its commit must fail.
    /// </summary>
    ConflictLoser,

    /// <summary>An explicit memtable flush of the default column family.</summary>
    Flush,

    /// <summary>An explicit full compaction.</summary>
    Compact
}
