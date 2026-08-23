using System.Globalization;
using System.Text;

namespace Pants.CompatibilityHarness.Internal;

internal sealed record ProcessResult(
    string DisplayCommand,
    string WorkingDirectory,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Elapsed)
{
    public string FormatEvidence(string heading)
    {
        var evidence = new StringBuilder();
        _ = evidence.AppendLine(heading);
        _ = evidence.Append("command: ").AppendLine(DisplayCommand);
        _ = evidence.Append("working directory: ").AppendLine(WorkingDirectory);
        _ = evidence.Append("exit code: ").AppendLine(ExitCode.ToString(CultureInfo.InvariantCulture));
        _ = evidence.AppendLine("stdout:");
        _ = evidence.AppendLine(StandardOutput.Length == 0 ? "<empty>" : StandardOutput);
        _ = evidence.AppendLine("stderr:");
        _ = evidence.Append(StandardError.Length == 0 ? "<empty>" : StandardError);
        return evidence.ToString();
    }
}
