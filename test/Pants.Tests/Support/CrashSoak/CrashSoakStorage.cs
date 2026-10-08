using System.Globalization;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>Where a run keeps its database and ledgers, and how every process opens the database.</summary>
static class CrashSoakStorage
{
    /// <summary>
    ///     How far each successive process's lease clock runs ahead of the previous one. It exceeds
    ///     the default lease TTL plus clock-skew tolerance, so every reopen after a kill takes the
    ///     dead writer's lease over through the ordinary expiry path instead of waiting it out or
    ///     editing the lease record.
    /// </summary>
    static readonly TimeSpan LeaseGenerationStep =
        PantsLeaseConfiguration.Default.TimeToLive +
        PantsLeaseConfiguration.Default.ClockSkewTolerance +
        TimeSpan.FromSeconds(5);

    public static string DatabasePath(string root) => Path.Combine(root, "db");

    public static string LedgerPath(string root, int cycle) =>
        Path.Combine(root, string.Create(CultureInfo.InvariantCulture, $"ledger-{cycle:000}.log"));

    /// <summary>The lease generation of the child that runs <paramref name="cycle" />.</summary>
    public static int ChildGeneration(int cycle) => (2 * cycle) + 1;

    /// <summary>The lease generation of the verifier that reopens after <paramref name="cycle" />.</summary>
    public static int VerifierGeneration(int cycle) => (2 * cycle) + 2;

    public static PantsOpenOptions CreateOptions(CrashSoakScenario scenario, string root, string cloudPrefix)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var path = DatabasePath(root);
        var options = scenario.Backend switch
        {
            CrashSoakBackend.Local => PantsOpenOptions.Local(path),
            CrashSoakBackend.SimulatedCloud => PantsOpenOptions.SimulatedCloud(path, "pants-crash-soak", cloudPrefix),
            CrashSoakBackend.Sqrzl => PantsOpenOptions.Cloud(path, CrashSoakSqrzl.CreateLocation(cloudPrefix)),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario.Backend, "Unknown backend.")
        };
        options = options
            .WithRecoveryPolicy(PantsRecoveryPolicy.Strict)
            .WithBackgroundCompaction(scenario.BackgroundCompaction)
            .WithMemtableLimits(16 * 1_024 * 1_024, scenario.MemtableFlushThresholdBytes);
        return scenario.FlushAfterWalRecords > 0
            ? options.WithFlushAfterWalRecordsForTesting(scenario.FlushAfterWalRecords)
            : options;
    }

    public static RuntimeDependencies CreateDependencies(int generation, IFailpointHandler? failpoints = null) =>
        new(failpoints, leaseClock: new OffsetPantsClock(LeaseGenerationStep * generation));

    /// <summary>
    ///     Deletes everything a replacement host would not have: the whole local cache, keeping only
    ///     the simulated cloud's object directory.
    /// </summary>
    public static void DiscardLocalCache(CrashSoakBackend backend, string root)
    {
        var path = DatabasePath(root);
        switch (backend)
        {
            case CrashSoakBackend.SimulatedCloud:
                foreach (var entry in Directory.EnumerateFileSystemEntries(path))
                {
                    if (StringComparer.Ordinal.Equals(Path.GetFileName(entry), "cloud_store"))
                    {
                        continue;
                    }

                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, true);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }

                break;
            case CrashSoakBackend.Sqrzl:
                Directory.Delete(path, true);
                break;
            default:
                throw new InvalidOperationException($"The {backend} backend has no cloud copy to recover from.");
        }
    }
}
