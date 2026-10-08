using System.Globalization;

namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>A killed child's ledger as the verifier reads it back.</summary>
sealed class CrashSoakLedger
{
    readonly Dictionary<int, CrashSoakOutcome> _outcomes;

    CrashSoakLedger(
        Dictionary<int, CrashSoakOutcome> outcomes,
        IReadOnlyList<string> errors,
        bool completed,
        string? killReason)
    {
        _outcomes = outcomes;
        Errors = errors;
        Completed = completed;
        KillReason = killReason;
    }

    /// <summary>Exceptions the child did not expect, each with the operation it interrupted.</summary>
    public IReadOnlyList<string> Errors { get; }

    public bool Completed { get; }

    /// <summary>Why the child killed itself, or null when it died without recording a reason.</summary>
    public string? KillReason { get; }

    public CrashSoakOutcome OutcomeOf(CrashSoakOperation operation) =>
        _outcomes.GetValueOrDefault(operation.Id, CrashSoakOutcome.NotDispatched);

    public int Count(CrashSoakOutcome outcome) => _outcomes.Values.Count(value => value == outcome);

    public static CrashSoakLedger Read(string path)
    {
        var outcomes = new Dictionary<int, CrashSoakOutcome>();
        var errors = new List<string>();
        var completed = false;
        string? killReason = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            switch (line[0])
            {
                case 'D':
                    outcomes[ParseId(line)] = CrashSoakOutcome.Unknown;
                    break;
                case 'A':
                    outcomes[ParseId(line)] = CrashSoakOutcome.Acked;
                    break;
                case 'F':
                    outcomes[ParseId(line)] = CrashSoakOutcome.Failed;
                    break;
                case 'E':
                    errors.Add(line[2..]);
                    break;
                case 'C':
                    completed = true;
                    break;
                case 'K':
                    killReason = line[2..];
                    break;
                default:
                    throw new InvalidDataException($"Unrecognized crash/soak ledger line '{line}'.");
            }
        }

        return new CrashSoakLedger(outcomes, errors, completed, killReason);
    }

    static int ParseId(string line)
    {
        var end = line.IndexOf(' ', 2);
        return int.Parse(end < 0 ? line.AsSpan(2) : line.AsSpan(2, end - 2), CultureInfo.InvariantCulture);
    }
}
