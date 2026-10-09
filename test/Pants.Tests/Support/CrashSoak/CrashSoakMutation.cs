using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>
///     One staged write. Keys and values are ASCII text so ordinal string order matches the engine's
///     byte order and a ledger violation can quote them directly.
/// </summary>
sealed record CrashSoakMutation(
    CrashSoakMutationKind Kind,
    string Key,
    string? EndKey = null,
    string? Value = null)
{
    public static CrashSoakMutation Put(string key, string value) =>
        new(CrashSoakMutationKind.Put, key, Value: value);

    public static CrashSoakMutation Insert(string key, string value) =>
        new(CrashSoakMutationKind.Insert, key, Value: value);

    public static CrashSoakMutation Delete(string key) =>
        new(CrashSoakMutationKind.Delete, key);

    public static CrashSoakMutation DeleteRange(string startInclusive, string endExclusive) =>
        new(CrashSoakMutationKind.DeleteRange, startInclusive, endExclusive);

    /// <summary>Whether this mutation decides <paramref name="key" />'s value.</summary>
    public bool Covers(string key) => Kind == CrashSoakMutationKind.DeleteRange
        ? StringComparer.Ordinal.Compare(Key, key) <= 0 &&
          StringComparer.Ordinal.Compare(key, EndKey) < 0
        : StringComparer.Ordinal.Equals(Key, key);

    /// <summary>The value <paramref name="key" /> holds after this mutation, or null when absent.</summary>
    public string? ValueAfter => Kind is CrashSoakMutationKind.Put or CrashSoakMutationKind.Insert
        ? Value
        : null;

    public void ApplyTo(IPantsTransaction transaction)
    {
        switch (Kind)
        {
            case CrashSoakMutationKind.Put:
                transaction.Put(TestBytes.FromString(Key), TestBytes.FromString(Value!));
                break;
            case CrashSoakMutationKind.Insert:
                transaction.Insert(TestBytes.FromString(Key), TestBytes.FromString(Value!));
                break;
            case CrashSoakMutationKind.Delete:
                transaction.Delete(TestBytes.FromString(Key));
                break;
            case CrashSoakMutationKind.DeleteRange:
                transaction.DeleteRange(TestBytes.FromString(Key), TestBytes.FromString(EndKey!));
                break;
            default:
                throw new InvalidOperationException($"Unknown mutation kind {Kind}.");
        }
    }

    public override string ToString() => Kind switch
    {
        CrashSoakMutationKind.DeleteRange => $"DeleteRange[{Key},{EndKey})",
        CrashSoakMutationKind.Delete => $"Delete({Key})",
        _ => $"{Kind}({Key}={Abbreviate(Value)})"
    };

    static string Abbreviate(string? value) =>
        value is null || value.Length <= 48 ? value ?? "<null>" : $"{value[..48]}…({value.Length})";
}
