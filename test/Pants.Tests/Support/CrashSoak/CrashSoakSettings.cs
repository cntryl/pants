using System.Globalization;

namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>
///     The opt-in soak configuration, read from environment variables. The bounded smoke run in the
///     default suite ignores all of these except <see cref="KeepFailuresVariable" />.
/// </summary>
static class CrashSoakSettings
{
    public const string EnabledVariable = "PANTS_CRASH_SOAK";
    public const string SeedsVariable = "PANTS_CRASH_SOAK_SEEDS";
    public const string DurationVariable = "PANTS_CRASH_SOAK_MINUTES";
    public const string CyclesVariable = "PANTS_CRASH_SOAK_CYCLES";
    public const string OperationsVariable = "PANTS_CRASH_SOAK_OPERATIONS";
    public const string BackendsVariable = "PANTS_CRASH_SOAK_BACKENDS";
    public const string KeepFailuresVariable = "PANTS_CRASH_SOAK_KEEP_FAILURES";

    const string SoakTestName = "Cntryl.Pants.Soak.PantsCrashSoakTests.ShouldHonorLedgerGivenOptInSoak";

    public static bool Enabled => IsSet(EnabledVariable);

    public static bool KeepFailures => IsSet(KeepFailuresVariable);

    public static int Cycles => ReadInt(CyclesVariable, 8);

    public static int OperationsPerCycle => ReadInt(OperationsVariable, 160);

    /// <summary>How long to keep drawing new seeds after the listed ones, or zero to stop after them.</summary>
    public static TimeSpan Duration => TimeSpan.FromMinutes(ReadInt(DurationVariable, 0));

    public static IReadOnlyList<CrashSoakBackend> Backends =>
        (Environment.GetEnvironmentVariable(BackendsVariable) ?? "Local,SimulatedCloud")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(static name => Enum.Parse<CrashSoakBackend>(name, true))
        .ToArray();

    /// <summary>
    ///     The listed seeds (<c>7</c>, <c>1,2,3</c>, or <c>100-199</c>), or twenty seeds from a
    ///     time-derived start when none are listed. Every failure message names the seed it ran.
    /// </summary>
    public static IReadOnlyList<int> Seeds
    {
        get
        {
            var listed = Environment.GetEnvironmentVariable(SeedsVariable);
            if (string.IsNullOrWhiteSpace(listed))
            {
                var first = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 1_000_000_000);
                return Enumerable.Range(first, 20).ToArray();
            }

            return listed
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(static part =>
                {
                    var dash = part.IndexOf('-', 1);
                    if (dash < 0)
                    {
                        return [int.Parse(part, CultureInfo.InvariantCulture)];
                    }

                    var first = int.Parse(part.AsSpan(0, dash), CultureInfo.InvariantCulture);
                    var last = int.Parse(part.AsSpan(dash + 1), CultureInfo.InvariantCulture);
                    return Enumerable.Range(first, last - first + 1);
                })
                .ToArray();
        }
    }

    public static string ReplayCommand(int seed, CrashSoakBackend backend, int cycles, int operationsPerCycle) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{EnabledVariable}=1 {SeedsVariable}={seed} {BackendsVariable}={backend} " +
            $"{CyclesVariable}={cycles} {OperationsVariable}={operationsPerCycle} " +
            $"dotnet test test/Pants.Tests/Pants.Tests.csproj --filter \"FullyQualifiedName={SoakTestName}\"");

    static bool IsSet(string variable) =>
        Environment.GetEnvironmentVariable(variable) is "1" or "true" or "TRUE" or "True";

    static int ReadInt(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
