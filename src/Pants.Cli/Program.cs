using System.Text.Json;
using System.Text.Json.Nodes;
using Cntryl.Pants.Storage;

namespace Cntryl.Pants;

internal static class Program
{
    const string HelpText = "pants storage diagnostics\n\nUsage:\n  pants verify [--json] <db-path>";
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    static async Task<int> Main(string[] args)
    {
        var command = PantsCliCommandLineParser.Parse(args);
        if (command.Failure is { } failure)
        {
            WriteError("usage", failure.Message, failure.Format);
            return 2;
        }

        if (command.IsHelp)
        {
            Console.Out.WriteLine(HelpText);
            return 0;
        }

        var outcome = await PantsDatabase.VerifyPathOutcomeAsync(command.Path!).ConfigureAwait(false);
        if (outcome.Report is { } report)
        {
            if (command.Format == PantsCliOutputFormat.Json)
            {
                var output = JsonSerializer.SerializeToNode(
                    report,
                    JsonOptions)!.AsObject();
                output["schema_version"] = 1;
                output["verification_scope"] = "local_path";
                Console.Out.WriteLine(output.ToJsonString(JsonOptions));
            }
            else
            {
                Console.Out.WriteLine($"health={report.Health} authoritative={report.Authoritative} manifest_epoch={report.ManifestEpoch}");
            }

            return outcome.ExitCode;
        }

        WriteError(
            ToErrorKind(outcome.Kind),
            outcome.Message ?? outcome.ErrorCode?.ToString() ?? "storage verification failed",
            command.Format);
        return outcome.ExitCode;
    }

    static void WriteError(string kind, string message, PantsCliOutputFormat format)
    {
        if (format == PantsCliOutputFormat.Json)
        {
            var output = new JsonObject
            {
                ["schema_version"] = 1,
                ["verification_scope"] = "local_path",
                ["status"] = "error",
                ["error_kind"] = kind,
                ["message"] = message
            };
            Console.Out.WriteLine(output.ToJsonString(JsonOptions));
        }
        else
        {
            Console.Error.WriteLine($"pants: {message}");
        }

    }

    static string ToErrorKind(PantsStorageVerificationOutcomeKind kind) => kind switch
    {
        PantsStorageVerificationOutcomeKind.Usage => "usage",
        PantsStorageVerificationOutcomeKind.Storage => "storage",
        PantsStorageVerificationOutcomeKind.Corruption => "corruption",
        _ => "internal"
    };
}
