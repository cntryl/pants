using System.Diagnostics;

namespace Cntryl.Pants.Benches.Tier4;

/// <summary>
/// Builds a <see cref="ProcessStartInfo"/> that re-invokes this same tool with a different mode
/// — shared by <see cref="ScaleLadderCrashCheck"/> and <see cref="ScaleLadderReopenProbe"/>.
/// This tool builds an apphost (a native executable, not just a DLL): when the current process
/// was itself launched as that apphost, <see cref="Environment.ProcessPath"/> already points at
/// it and does **not** want the assembly path as an argument (the apphost already knows which
/// DLL to run) — passing it anyway silently shifts every subsequent argument by one, so the mode
/// name never matches. Conversely, a framework-dependent launch such as
/// <c>dotnet Cntryl.Pants.Benches.dll</c> leaves <see cref="Environment.ProcessPath"/> pointing at
/// the dotnet muxer even when <c>DOTNET_HOST_PATH</c> is absent; that mode must prepend the
/// assembly path.
/// </summary>
static class ScaleLadderChildProcess
{
    public static ProcessStartInfo Create(params string[] args)
    {
        return CreateForTesting(
            Environment.ProcessPath ?? "dotnet",
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            typeof(ScaleLadderChildProcess).Assembly.Location,
            args);
    }

    internal static ProcessStartInfo CreateForTesting(
        string processPath,
        string? dotnetHostPath,
        string assemblyPath,
        params string[] args)
    {
        var executable = dotnetHostPath ?? processPath;
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (dotnetHostPath is not null || IsDotnetMuxer(executable))
        {
            start.ArgumentList.Add(assemblyPath);
        }

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return start;
    }

    static bool IsDotnetMuxer(string path) =>
        StringComparer.OrdinalIgnoreCase.Equals(
            Path.GetFileNameWithoutExtension(path),
            "dotnet");
}
