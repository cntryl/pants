using System.Diagnostics;
using System.Globalization;

namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>What the verifier tells one child process to run, passed through its environment.</summary>
sealed record CrashSoakChildSpec(
    int Seed,
    CrashSoakBackend Backend,
    int OperationsPerCycle,
    int Cycle,
    string Root,
    string CloudPrefix)
{
    const string SpecVariable = "PANTS_CRASH_SOAK_CHILD";
    const string RootVariable = "PANTS_CRASH_SOAK_CHILD_ROOT";
    const string PrefixVariable = "PANTS_CRASH_SOAK_CHILD_PREFIX";

    public CrashSoakScenario Scenario => CrashSoakScenario.FromSeed(Seed, Backend, OperationsPerCycle);

    /// <summary>The spec this process was launched with, or null outside a crash/soak child.</summary>
    public static CrashSoakChildSpec? FromEnvironment()
    {
        var spec = Environment.GetEnvironmentVariable(SpecVariable);
        if (string.IsNullOrEmpty(spec))
        {
            return null;
        }

        var fields = spec.Split(':');
        return new CrashSoakChildSpec(
            int.Parse(fields[0], CultureInfo.InvariantCulture),
            Enum.Parse<CrashSoakBackend>(fields[1]),
            int.Parse(fields[2], CultureInfo.InvariantCulture),
            int.Parse(fields[3], CultureInfo.InvariantCulture),
            Environment.GetEnvironmentVariable(RootVariable) ??
            throw new InvalidOperationException($"{RootVariable} is required."),
            Environment.GetEnvironmentVariable(PrefixVariable) ?? string.Empty);
    }

    public void ApplyTo(ProcessStartInfo start)
    {
        ArgumentNullException.ThrowIfNull(start);
        start.Environment[SpecVariable] = string.Create(
            CultureInfo.InvariantCulture,
            $"{Seed}:{Backend}:{OperationsPerCycle}:{Cycle}");
        start.Environment[RootVariable] = Root;
        start.Environment[PrefixVariable] = CloudPrefix;
    }
}
