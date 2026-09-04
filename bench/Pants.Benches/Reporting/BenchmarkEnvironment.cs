using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cntryl.Pants.Reporting;

static class BenchmarkEnvironment
{
    public static BenchmarkRunMetadata Capture(string measurementClass, int expectedScenarioCount) => new(
        "pants",
        Run("git", "rev-parse HEAD"),
        RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(),
        ReadCpu(),
        RuntimeInformation.FrameworkDescription,
        measurementClass,
        expectedScenarioCount);

    static string ReadCpu()
    {
        var configured = Environment.GetEnvironmentVariable("PANTS_BENCH_CPU");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        if (OperatingSystem.IsMacOS())
        {
            return Run("sysctl", "-n machdep.cpu.brand_string");
        }

        if (OperatingSystem.IsLinux())
        {
            var modelLine = File.ReadLines("/proc/cpuinfo")
                .FirstOrDefault(line => line.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
            if (modelLine is not null)
            {
                return modelLine.Split(':', 2)[1].Trim();
            }
        }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ??
               RuntimeInformation.ProcessArchitecture.ToString();
    }

    static string Run(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(
                $"'{fileName} {arguments}' failed with exit code {process.ExitCode}: {error.Trim()}");
        }

        return output.Trim();
    }
}
