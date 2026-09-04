using System.ComponentModel;
using System.Diagnostics;

namespace Cntryl.Pants.CompatibilityHarness.Internal;

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var fullWorkingDirectory = Path.GetFullPath(workingDirectory);
        var displayCommand = FormatCommand(executable, arguments);
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = fullWorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The operating system did not start the process.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            throw new InvalidOperationException(
                $"Failed to start command '{displayCommand}' in '{fullWorkingDirectory}'.",
                exception);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        stopwatch.Stop();
        var result = new ProcessResult(
            displayCommand,
            fullWorkingDirectory,
            process.ExitCode,
            output,
            error,
            stopwatch.Elapsed);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.FormatEvidence("Child process failed."));
        }

        return result;
    }

    static string FormatCommand(string executable, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { QuoteArgument(executable) }.Concat(arguments.Select(QuoteArgument)));

    static string QuoteArgument(string argument)
    {
        if (argument.Length != 0 && argument.All(IsUnquotedCharacter))
        {
            return argument;
        }

        return $"'{argument.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    static bool IsUnquotedCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '_' or '-' or '.' or '/' or ':';
}
