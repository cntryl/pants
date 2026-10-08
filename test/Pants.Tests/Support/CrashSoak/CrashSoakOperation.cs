namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>One ledgered unit of work: a transaction, or a maintenance request with no logical effect.</summary>
sealed record CrashSoakOperation(
    int Id,
    int Lane,
    CrashSoakOperationKind Kind,
    PantsDurability Durability,
    IReadOnlyList<CrashSoakMutation> Mutations)
{
    /// <summary>
    ///     Whether no outcome can make this operation's effects visible: it is rolled back, or its
    ///     commit is rejected by construction.
    /// </summary>
    public bool IsDoomed => Kind is CrashSoakOperationKind.Rollback or
        CrashSoakOperationKind.AssertionFailure or
        CrashSoakOperationKind.ConflictLoser;

    public bool IsMaintenance => Kind is CrashSoakOperationKind.Flush or CrashSoakOperationKind.Compact;

    /// <summary>The value <paramref name="key" /> holds after this operation, when it decides that key at all.</summary>
    public bool TryGetEffect(string key, out string? value)
    {
        var touched = false;
        value = null;
        foreach (var mutation in Mutations)
        {
            if (!mutation.Covers(key))
            {
                continue;
            }

            touched = true;
            value = mutation.ValueAfter;
        }

        return touched;
    }

    public override string ToString() =>
        $"op {Id} lane {Lane} {Kind} {Durability}: [{string.Join(", ", Mutations)}]";
}
