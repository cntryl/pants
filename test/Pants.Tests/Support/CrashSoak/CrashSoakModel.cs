namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>
///     The verifier's knowledge of the database: the state the last verified reopen observed, plus
///     every key a workload has written. It turns a cycle's ledger into, for each key, the values
///     recovery may legally produce, and checks an observed reopen against them.
/// </summary>
/// <remarks>
///     For each key, the operations that may decide it are the dispatched, non-doomed operations of
///     its lane in lane order. The last one acknowledged with a durability the crash cannot undo
///     fixes a floor; it and every later candidate are allowed, and nothing earlier is. Operations
///     whose outcome is unknown, or whose acknowledgement promised no crash durability, may each be
///     visible or not, but never partially.
/// </remarks>
sealed class CrashSoakModel
{
    readonly Dictionary<string, string> _baseline = new(StringComparer.Ordinal);
    readonly SortedSet<string> _keys = new(StringComparer.Ordinal);

    public CrashSoakModel(CrashSoakScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        _keys.UnionWith(scenario.LaneKeys);
    }

    /// <summary>Every key any workload so far may have written, in engine order.</summary>
    public IReadOnlyCollection<string> Keys => _keys;

    /// <summary>Adds the keys a cycle may write, before the verifier reads them back.</summary>
    public void Include(CrashSoakCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        foreach (var mutation in cycle.Operations.SelectMany(static operation => operation.Mutations))
        {
            if (mutation.Kind != CrashSoakMutationKind.DeleteRange)
            {
                _keys.Add(mutation.Key);
            }
        }
    }

    public List<string> Verify(
        CrashSoakCycle cycle,
        CrashSoakLedger ledger,
        bool localCacheLost,
        IReadOnlyDictionary<string, string> observed,
        IReadOnlyList<KeyValuePair<string, string>> scanned)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(scanned);
        var violations = new List<string>();
        foreach (var operation in cycle.Operations)
        {
            if (operation.IsDoomed && ledger.OutcomeOf(operation) == CrashSoakOutcome.Acked)
            {
                violations.Add($"{operation} was acknowledged although its commit must be rejected.");
            }
        }

        var candidatesByKey = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        foreach (var key in _keys)
        {
            var candidates = CandidatesFor(key, cycle, ledger, localCacheLost);
            candidatesByKey[key] = candidates;
            var actual = observed.GetValueOrDefault(key);
            if (!candidates.Any(candidate => StringComparer.Ordinal.Equals(candidate.Value, actual)))
            {
                violations.Add(
                    $"key {key} recovered as {Describe(actual)}, written by {Writer(actual, cycle, ledger)}; " +
                    $"allowed: {string.Join(" | ", candidates.Select(candidate => candidate.Describe(ledger)))}.");
            }
        }

        violations.AddRange(CheckAtomicity(cycle, ledger, observed, candidatesByKey));
        violations.AddRange(CheckScan(observed, scanned));
        return violations;
    }

    /// <summary>Makes the verified reopen the floor for the next cycle.</summary>
    public void Adopt(IReadOnlyDictionary<string, string> observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        _baseline.Clear();
        foreach (var (key, value) in observed)
        {
            _baseline[key] = value;
        }
    }

    static bool SurvivesCrash(PantsDurability durability, bool localCacheLost) => durability switch
    {
        PantsDurability.Sync => true,
        PantsDurability.CloudStrict => true,
        PantsDurability.CloudAsync => !localCacheLost,
        _ => false
    };

    List<Candidate> CandidatesFor(string key, CrashSoakCycle cycle, CrashSoakLedger ledger, bool localCacheLost)
    {
        var candidates = new List<Candidate> { new(null, _baseline.GetValueOrDefault(key)) };
        foreach (var operation in cycle.Operations.OrderBy(static operation => operation.Id))
        {
            var outcome = ledger.OutcomeOf(operation);
            if (operation.IsDoomed ||
                outcome is CrashSoakOutcome.NotDispatched or CrashSoakOutcome.Failed ||
                !operation.TryGetEffect(key, out var value))
            {
                continue;
            }

            if (outcome == CrashSoakOutcome.Acked && SurvivesCrash(operation.Durability, localCacheLost))
            {
                candidates.Clear();
            }

            candidates.Add(new Candidate(operation, value));
        }

        return candidates;
    }

    static IEnumerable<string> CheckAtomicity(
        CrashSoakCycle cycle,
        CrashSoakLedger ledger,
        IReadOnlyDictionary<string, string> observed,
        Dictionary<string, List<Candidate>> candidatesByKey)
    {
        foreach (var operation in cycle.Operations)
        {
            var applied = new List<string>();
            var missing = new List<string>();
            foreach (var (key, candidates) in candidatesByKey)
            {
                var position = candidates.FindIndex(candidate => candidate.Operation == operation);
                if (position < 0)
                {
                    continue;
                }

                var effect = candidates[position].Value;
                var actual = observed.GetValueOrDefault(key);
                if (StringComparer.Ordinal.Equals(actual, effect))
                {
                    if (candidates.Count(candidate => StringComparer.Ordinal.Equals(candidate.Value, effect)) == 1)
                    {
                        applied.Add(key);
                    }
                }
                else if (position == candidates.Count - 1)
                {
                    missing.Add(key);
                }
            }

            if (applied.Count > 0 && missing.Count > 0)
            {
                yield return
                    $"{operation} ({ledger.OutcomeOf(operation)}) is only partly visible: applied to " +
                    $"{string.Join(", ", applied)} but missing from {string.Join(", ", missing)}.";
            }
        }
    }

    IEnumerable<string> CheckScan(
        IReadOnlyDictionary<string, string> observed,
        IReadOnlyList<KeyValuePair<string, string>> scanned)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in scanned)
        {
            seen.Add(key);
            if (!_keys.Contains(key))
            {
                yield return $"a scan returned {key}, which no workload wrote.";
            }
            else if (!StringComparer.Ordinal.Equals(observed.GetValueOrDefault(key), value))
            {
                yield return
                    $"a scan returned {key}={Describe(value)} but a point read returned " +
                    $"{Describe(observed.GetValueOrDefault(key))}.";
            }
        }

        foreach (var key in observed.Keys.Where(key => !seen.Contains(key)))
        {
            yield return $"a point read found {key} but a full scan omitted it.";
        }
    }

    static string Writer(string? value, CrashSoakCycle cycle, CrashSoakLedger ledger)
    {
        if (value is null)
        {
            return "a delete";
        }

        var writer = cycle.Operations.FirstOrDefault(operation =>
            operation.Mutations.Any(mutation => StringComparer.Ordinal.Equals(mutation.Value, value)));
        return writer is null
            ? "an earlier cycle"
            : $"{writer} ({ledger.OutcomeOf(writer)})";
    }

    static string Describe(string? value) =>
        value is null ? "<absent>" : value.Length <= 40 ? value : $"{value[..40]}…({value.Length})";

    sealed record Candidate(CrashSoakOperation? Operation, string? Value)
    {
        public string Describe(CrashSoakLedger ledger) => Operation is null
            ? $"{CrashSoakModel.Describe(Value)} from the previous reopen"
            : $"{CrashSoakModel.Describe(Value)} from op {Operation.Id} " +
              $"({Operation.Durability}, {ledger.OutcomeOf(Operation)})";
    }
}
