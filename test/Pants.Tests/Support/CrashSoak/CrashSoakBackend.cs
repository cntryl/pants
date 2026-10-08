namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>The storage a crash/soak run drives.</summary>
public enum CrashSoakBackend
{
    /// <summary>Local disk storage, committing with <c>Sync</c>, <c>Buffered</c>, and <c>BestEffort</c>.</summary>
    Local,

    /// <summary>The file-backed simulated cloud, committing with <c>CloudStrict</c>, <c>CloudAsync</c>, and <c>BestEffort</c>.</summary>
    SimulatedCloud,

    /// <summary>The Sqrzl emulator through its S3-compatible API; requires a running emulator.</summary>
    Sqrzl
}
