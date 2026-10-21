namespace Cntryl.Pants;

internal enum PantsCliOutputFormat
{
    Plain,
    Json
}

internal sealed record PantsCliParseFailure(string Message, PantsCliOutputFormat Format);

internal sealed record PantsCliParsedCommand(
    string? Path,
    PantsCliOutputFormat Format,
    bool IsHelp,
    PantsCliParseFailure? Failure)
{
    public static PantsCliParsedCommand Help() => new(null, PantsCliOutputFormat.Plain, true, null);

    public static PantsCliParsedCommand Verify(string path, PantsCliOutputFormat format) =>
        new(path, format, false, null);

    public static PantsCliParsedCommand Error(string message, PantsCliOutputFormat format) =>
        new(null, format, false, new PantsCliParseFailure(message, format));
}

internal static class PantsCliCommandLineParser
{
    const string Usage = "usage: pants verify [--json] <db-path>";

    public static PantsCliParsedCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var format = args.Contains("--json", StringComparer.Ordinal)
            ? PantsCliOutputFormat.Json
            : PantsCliOutputFormat.Plain;

        if (args.Count == 0)
        {
            return PantsCliParsedCommand.Error(Usage, format);
        }

        var command = args[0];
        if (command is "--help" or "-h")
        {
            return PantsCliParsedCommand.Help();
        }

        if (command != "verify")
        {
            return PantsCliParsedCommand.Error($"unknown command '{command}'; {Usage}", format);
        }

        string? path = null;
        foreach (var arg in args.Skip(1))
        {
            if (arg == "--json")
            {
                continue;
            }

            if (arg is "--help" or "-h")
            {
                return PantsCliParsedCommand.Help();
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                return PantsCliParsedCommand.Error($"unknown flag '{arg}'; {Usage}", format);
            }

            if (path is not null)
            {
                return PantsCliParsedCommand.Error(Usage, format);
            }

            path = arg;
        }

        return path is null
            ? PantsCliParsedCommand.Error(Usage, format)
            : PantsCliParsedCommand.Verify(path, format);
    }
}
