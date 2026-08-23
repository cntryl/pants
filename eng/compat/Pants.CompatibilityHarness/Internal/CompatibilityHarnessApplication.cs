using System.Text;

namespace Pants.CompatibilityHarness.Internal;

internal static class CompatibilityHarnessApplication
{
    const int ExecutionFailureExitCode = 1;
    const int UsageFailureExitCode = 2;
    const int CanceledExitCode = 130;
    const string InteropColumnFamilyName = "interop";
    const string SimulatedCloudBucket = "pants-compat";
    const string SimulatedCloudPrefix = "database/";

    const string Usage = """
        Usage:
          Pants.CompatibilityHarness local-create <db> <producer>
          Pants.CompatibilityHarness local-mutate <db> <producer>
          Pants.CompatibilityHarness local-assert <db> <comma-separated-producers>
          Pants.CompatibilityHarness local-verify <db>
          Pants.CompatibilityHarness cloud-create <db> <producer>
          Pants.CompatibilityHarness cloud-mutate <db> <producer>
          Pants.CompatibilityHarness cloud-assert <db> <comma-separated-producers>
          Pants.CompatibilityHarness cloud-verify <db>
          Pants.CompatibilityHarness qualify --midge <checkout>
          Pants.CompatibilityHarness refresh [--check | --force] --midge <checkout>
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var command = default(CompatibilityCommand);
        var midgeCheckoutCommand = default(MidgeCheckoutCommand);
        var parsingError = string.Empty;
        var parsed = args.Length != 0 && args[0] is "qualify" or "refresh"
            ? TryParseMidgeCheckoutCommand(args, out midgeCheckoutCommand, out parsingError)
            : TryParse(args, out command, out parsingError);
        if (!parsed)
        {
            Console.Error.WriteLine($"error: {parsingError}");
            Console.Error.WriteLine(Usage);
            return UsageFailureExitCode;
        }

        using var cancellation = new CancellationTokenSource();
        var cancelHandler = new ConsoleCancelEventHandler((_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        });
        Console.CancelKeyPress += cancelHandler;

        try
        {
            if (midgeCheckoutCommand is not null)
            {
                if (midgeCheckoutCommand.Operation == MidgeCheckoutOperation.Qualify)
                {
                    await QualificationRunner.RunAsync(
                        midgeCheckoutCommand.CheckoutPath,
                        cancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    await FixtureRefreshRunner.RunAsync(
                        midgeCheckoutCommand.CheckoutPath,
                        midgeCheckoutCommand.ForceRefresh,
                        midgeCheckoutCommand.CheckBaseline,
                        cancellation.Token).ConfigureAwait(false);
                }
            }
            else
            {
                await ExecuteAsync(command!, cancellation.Token).ConfigureAwait(false);
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("error: operation canceled.");
            return CanceledExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.GetType().Name}: {exception.Message}");
            return ExecutionFailureExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    static async Task ExecuteAsync(
        CompatibilityCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Operation == CompatibilityOperation.Verify)
        {
            await VerifyAsync(command.DatabasePath, cancellationToken).ConfigureAwait(false);
            Console.WriteLine("verification succeeded");
            return;
        }

        var options = command.StorageMode == CompatibilityStorageMode.Local
            ? PantsOpenOptions.Local(command.DatabasePath)
            : PantsOpenOptions.SimulatedCloud(
                command.DatabasePath,
                SimulatedCloudBucket,
                SimulatedCloudPrefix);

        await using var database = await PantsDatabase.OpenAsync(options, cancellationToken)
            .ConfigureAwait(false);

        switch (command.Operation)
        {
            case CompatibilityOperation.Create:
                _ = await WriteAsync(
                    database,
                    command.Producers[0],
                    GetWriteOptions(command.StorageMode),
                    cancellationToken).ConfigureAwait(false);
                break;
            case CompatibilityOperation.Mutate:
                var interopColumnFamily = await WriteAsync(
                    database,
                    command.Producers[0],
                    GetWriteOptions(command.StorageMode),
                    cancellationToken).ConfigureAwait(false);
                await database.FlushAsync(database.DefaultColumnFamily, cancellationToken)
                    .ConfigureAwait(false);
                await database.FlushAsync(interopColumnFamily, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case CompatibilityOperation.Assert:
                await AssertAsync(database, command.Producers, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unsupported operation '{command.Operation}'.");
        }

        await database.ShutdownAsync(options.ShutdownTimeout, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"{command.Operation.ToString().ToLowerInvariant()} succeeded");
    }

    static async Task<IPantsColumnFamily> WriteAsync(
        IPantsDatabase database,
        string producer,
        PantsWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        var interopColumnFamily = await database.GetColumnFamilyAsync(
            InteropColumnFamilyName,
            cancellationToken).ConfigureAwait(false)
            ?? await database.CreateColumnFamilyAsync(
                InteropColumnFamilyName,
                cancellationToken).ConfigureAwait(false);
        var value = Encoding.UTF8.GetBytes($"created-by-{producer}");

        await PutAsync(
            database,
            database.DefaultColumnFamily,
            Encoding.UTF8.GetBytes($"compat/{producer}"),
            value,
            writeOptions,
            cancellationToken).ConfigureAwait(false);
        await PutAsync(
            database,
            interopColumnFamily,
            Encoding.UTF8.GetBytes($"compat-cf/{producer}"),
            value,
            writeOptions,
            cancellationToken).ConfigureAwait(false);
        return interopColumnFamily;
    }

    static async Task PutAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        PantsWriteOptions writeOptions,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.BeginTransactionAsync(
            columnFamily,
            PantsTransactionMode.ReadWrite,
            cancellationToken).ConfigureAwait(false);
        transaction.Put(key, value);
        await transaction.CommitAsync(writeOptions, cancellationToken).ConfigureAwait(false);
    }

    static async Task AssertAsync(
        IPantsDatabase database,
        IReadOnlyList<string> producers,
        CancellationToken cancellationToken)
    {
        var interopColumnFamily = await database.GetColumnFamilyAsync(
            InteropColumnFamilyName,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Required column family '{InteropColumnFamilyName}' does not exist.");

        await AssertColumnFamilyAsync(
            database,
            database.DefaultColumnFamily,
            "compat/",
            producers,
            cancellationToken).ConfigureAwait(false);
        await AssertColumnFamilyAsync(
            database,
            interopColumnFamily,
            "compat-cf/",
            producers,
            cancellationToken).ConfigureAwait(false);
    }

    static async Task AssertColumnFamilyAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        string keyPrefix,
        IReadOnlyList<string> producers,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.BeginTransactionAsync(
            columnFamily,
            PantsTransactionMode.ReadOnly,
            cancellationToken).ConfigureAwait(false);

        foreach (var producer in producers)
        {
            var keyText = $"{keyPrefix}{producer}";
            var expected = Encoding.UTF8.GetBytes($"created-by-{producer}");
            var actual = await transaction.GetAsync(
                Encoding.UTF8.GetBytes(keyText),
                cancellationToken).ConfigureAwait(false);

            if (actual is null)
            {
                throw new InvalidOperationException(
                    $"Missing key '{keyText}' in column family '{columnFamily.Name}'.");
            }

            if (!actual.Value.Span.SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    $"Unexpected value for key '{keyText}' in column family '{columnFamily.Name}'.");
            }
        }
    }

    static async Task VerifyAsync(string databasePath, CancellationToken cancellationToken)
    {
        var report = await PantsDatabase.VerifyPathAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);

        if (report.Health != PantsEngineHealth.Healthy)
        {
            throw new InvalidOperationException(
                $"Storage verification health was '{report.Health}', not '{PantsEngineHealth.Healthy}'.");
        }

        if (!report.Authoritative)
        {
            throw new InvalidOperationException("Storage verification report was not authoritative.");
        }
    }

    static PantsWriteOptions GetWriteOptions(CompatibilityStorageMode storageMode) =>
        storageMode == CompatibilityStorageMode.Local
            ? PantsWriteOptions.Sync
            : PantsWriteOptions.CloudStrict;

    static bool TryParse(
        string[] args,
        out CompatibilityCommand? command,
        out string error)
    {
        command = null!;
        error = string.Empty;

        if (args.Length == 0)
        {
            error = "A command is required.";
            return false;
        }

        CompatibilityStorageMode storageMode;
        CompatibilityOperation operation;
        switch (args[0])
        {
            case "local-create":
                storageMode = CompatibilityStorageMode.Local;
                operation = CompatibilityOperation.Create;
                break;
            case "local-mutate":
                storageMode = CompatibilityStorageMode.Local;
                operation = CompatibilityOperation.Mutate;
                break;
            case "local-assert":
                storageMode = CompatibilityStorageMode.Local;
                operation = CompatibilityOperation.Assert;
                break;
            case "local-verify":
                storageMode = CompatibilityStorageMode.Local;
                operation = CompatibilityOperation.Verify;
                break;
            case "cloud-create":
                storageMode = CompatibilityStorageMode.Cloud;
                operation = CompatibilityOperation.Create;
                break;
            case "cloud-mutate":
                storageMode = CompatibilityStorageMode.Cloud;
                operation = CompatibilityOperation.Mutate;
                break;
            case "cloud-assert":
                storageMode = CompatibilityStorageMode.Cloud;
                operation = CompatibilityOperation.Assert;
                break;
            case "cloud-verify":
                storageMode = CompatibilityStorageMode.Cloud;
                operation = CompatibilityOperation.Verify;
                break;
            default:
                error = $"Unknown command '{args[0]}'.";
                return false;
        }

        var expectedArgumentCount = operation == CompatibilityOperation.Verify ? 2 : 3;
        if (args.Length != expectedArgumentCount)
        {
            error = $"Command '{args[0]}' expects {expectedArgumentCount - 1} argument(s); "
                + $"received {args.Length - 1}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(args[1]))
        {
            error = "The database path must not be empty.";
            return false;
        }

        if (operation == CompatibilityOperation.Verify)
        {
            command = new CompatibilityCommand(storageMode, operation, args[1], []);
            return true;
        }

        if (!TryParseProducers(
                args[2],
                operation == CompatibilityOperation.Assert,
                out var producers,
                out error))
        {
            return false;
        }

        command = new CompatibilityCommand(storageMode, operation, args[1], producers);
        return true;
    }

    static bool TryParseMidgeCheckoutCommand(
        string[] args,
        out MidgeCheckoutCommand? command,
        out string error)
    {
        command = null;
        error = string.Empty;
        var refresh = args[0] == "refresh";
        var refreshMode = refresh && args.Length == 4 ? args[1] : null;
        var forceRefresh = refreshMode == "--force";
        var checkBaseline = refreshMode == "--check";
        var hasRefreshMode = forceRefresh || checkBaseline;
        var optionIndex = hasRefreshMode ? 2 : 1;
        var expectedLength = hasRefreshMode ? 4 : 3;
        if (args.Length != expectedLength)
        {
            error = refresh
                ? $"Command '{args[0]}' expects '--midge <checkout>' with an optional leading "
                    + $"'--check' or '--force'; received {args.Length - 1} argument(s)."
                : $"Command '{args[0]}' expects 2 arguments; received {args.Length - 1}.";
            return false;
        }

        if (!StringComparer.Ordinal.Equals(args[optionIndex], "--midge"))
        {
            error = $"Unknown {args[0]} option '{args[optionIndex]}'; expected '--midge'.";
            return false;
        }

        var checkoutIndex = optionIndex + 1;
        if (string.IsNullOrWhiteSpace(args[checkoutIndex]))
        {
            error = "The Midge checkout path must not be empty.";
            return false;
        }

        var operation = args[0] == "qualify"
            ? MidgeCheckoutOperation.Qualify
            : MidgeCheckoutOperation.Refresh;
        command = new MidgeCheckoutCommand(
            operation,
            args[checkoutIndex],
            forceRefresh,
            checkBaseline);
        return true;
    }

    static bool TryParseProducers(
        string argument,
        bool isList,
        out IReadOnlyList<string> producers,
        out string error)
    {
        var parts = isList
            ? argument.Split(',', StringSplitOptions.None)
            : [argument];
        var parsed = new string[parts.Length];

        for (var index = 0; index < parts.Length; index++)
        {
            var producer = parts[index].Trim();
            if (producer.Length == 0)
            {
                producers = [];
                error = "Producer names must not be empty.";
                return false;
            }

            if (producer.Contains(',', StringComparison.Ordinal))
            {
                producers = [];
                error = "A producer name must not contain a comma.";
                return false;
            }

            parsed[index] = producer;
        }

        producers = parsed;
        error = string.Empty;
        return true;
    }
}
