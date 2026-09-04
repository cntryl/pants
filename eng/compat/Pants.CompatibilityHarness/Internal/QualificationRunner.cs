using System.Diagnostics;
using System.Globalization;

namespace Cntryl.Pants.CompatibilityHarness.Internal;

internal static class QualificationRunner
{
    public static async Task RunAsync(
        string midgeCheckoutPath,
        CancellationToken cancellationToken)
    {
        using var temporaryDirectory = new QualificationTemporaryDirectory();
        var midge = await MidgeCheckoutBuilder.BuildAsync(
            midgeCheckoutPath,
            temporaryDirectory.RootPath,
            MidgeDriverBuildMode.Qualification,
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine(
            $"Midge driver build: {FormatSeconds(midge.BuildTime)}s "
            + $"({MidgeCheckoutBuilder.RequiredCommit})");

        var pantsAssemblyPath = typeof(QualificationRunner).Assembly.Location;
        if (pantsAssemblyPath.Length == 0 || !File.Exists(pantsAssemblyPath))
        {
            throw new FileNotFoundException(
                $"Could not resolve this compatibility harness assembly at '{pantsAssemblyPath}'.",
                pantsAssemblyPath);
        }

        await RunTimedScenarioAsync(
            "local-midge-first",
            cloud: false,
            midgeFirst: true,
            temporaryDirectory.RootPath,
            pantsAssemblyPath,
            midge,
            cancellationToken).ConfigureAwait(false);
        await RunTimedScenarioAsync(
            "local-pants-first",
            cloud: false,
            midgeFirst: false,
            temporaryDirectory.RootPath,
            pantsAssemblyPath,
            midge,
            cancellationToken).ConfigureAwait(false);
        await RunTimedScenarioAsync(
            "cloud-midge-first",
            cloud: true,
            midgeFirst: true,
            temporaryDirectory.RootPath,
            pantsAssemblyPath,
            midge,
            cancellationToken).ConfigureAwait(false);
        await RunTimedScenarioAsync(
            "cloud-pants-first",
            cloud: true,
            midgeFirst: false,
            temporaryDirectory.RootPath,
            pantsAssemblyPath,
            midge,
            cancellationToken).ConfigureAwait(false);
    }

    static async Task RunTimedScenarioAsync(
        string scenarioName,
        bool cloud,
        bool midgeFirst,
        string temporaryRoot,
        string pantsAssemblyPath,
        MidgeDriverBuild midge,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await RunScenarioAsync(
            scenarioName,
            cloud,
            midgeFirst,
            temporaryRoot,
            pantsAssemblyPath,
            midge,
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        Console.WriteLine($"scenario {scenarioName}: {FormatSeconds(stopwatch.Elapsed)}s");
    }

    static async Task RunScenarioAsync(
        string scenarioName,
        bool cloud,
        bool midgeFirst,
        string temporaryRoot,
        string pantsAssemblyPath,
        MidgeDriverBuild midge,
        CancellationToken cancellationToken)
    {
        var databasePath = Path.Combine(temporaryRoot, "scenarios", scenarioName, "database");
        var mode = cloud ? "cloud" : "local";
        var firstEngineName = midgeFirst ? "midge" : "pants";
        var secondEngineName = midgeFirst ? "pants" : "midge";
        var firstEngineIsMidge = midgeFirst;
        var secondEngineIsMidge = !midgeFirst;
        var firstWalProducer = $"{scenarioName}-{firstEngineName}-wal";
        var secondSstProducer = $"{scenarioName}-{secondEngineName}-sst";
        var firstSstProducer = $"{scenarioName}-{firstEngineName}-sst";
        var firstProducerList = firstWalProducer;
        var firstMutationProducerList = string.Join(',', firstWalProducer, secondSstProducer);
        var allProducerList = string.Join(
            ',',
            firstWalProducer,
            secondSstProducer,
            firstSstProducer);

        _ = await RunEngineAsync(
            firstEngineIsMidge,
            $"{mode}-create",
            databasePath,
            firstWalProducer,
            temporaryRoot,
            pantsAssemblyPath,
            midge,
            cancellationToken).ConfigureAwait(false);
        // Current Midge publishes CloudStrict transactions as SSTs before returning, while
        // both engines leave local creates in the WAL and Pants may do the same in simulated
        // cloud mode. Preserve the WAL-only proof where the producer contract permits it and
        // require durable cloud publication for the Midge-first cloud boundary.
        AssertSstFileCount(
            databasePath,
            requireNone: !cloud || !firstEngineIsMidge,
            scenarioName,
            "create");
        if (!cloud)
        {
            await RunOfflineVerifiersAsync(
                mode,
                databasePath,
                temporaryRoot,
                pantsAssemblyPath,
                midge,
                cancellationToken).ConfigureAwait(false);
        }

        _ = await RunEngineAsync(
            secondEngineIsMidge,
            $"{mode}-assert",
            databasePath,
            firstProducerList,
            temporaryRoot,
            pantsAssemblyPath,
            midge,
            cancellationToken).ConfigureAwait(false);
        _ = await RunEngineAsync(
            secondEngineIsMidge,
            $"{mode}-mutate",
            databasePath,
            secondSstProducer,
            temporaryRoot,
            pantsAssemblyPath,
            midge,
            cancellationToken).ConfigureAwait(false);
        AssertSstFileCount(databasePath, requireNone: false, scenarioName, "first mutate");
        await RunOfflineVerifiersAsync(
            mode,
            databasePath,
            temporaryRoot,
            pantsAssemblyPath,
            midge,
            cancellationToken).ConfigureAwait(false);

        _ = await RunEngineAsync(
            firstEngineIsMidge,
            $"{mode}-assert",
            databasePath,
            firstMutationProducerList,
            temporaryRoot,
            pantsAssemblyPath,
            midge,
            cancellationToken).ConfigureAwait(false);
        var firstMutationSstFingerprint = SstFileSetFingerprint.Compute(databasePath);
        _ = await RunEngineAsync(
            firstEngineIsMidge,
            $"{mode}-mutate",
            databasePath,
            firstSstProducer,
            temporaryRoot,
            pantsAssemblyPath,
            midge,
            cancellationToken).ConfigureAwait(false);
        AssertSstFileCount(databasePath, requireNone: false, scenarioName, "second mutate");
        var secondMutationSstFingerprint = SstFileSetFingerprint.Compute(databasePath);
        if (StringComparer.Ordinal.Equals(
                firstMutationSstFingerprint,
                secondMutationSstFingerprint))
        {
            throw new InvalidOperationException(
                $"Scenario '{scenarioName}' did not publish a distinct SST set during the "
                + "second engine mutation.");
        }

        if (!cloud)
        {
            await RunOfflineVerifiersAsync(
                mode,
                databasePath,
                temporaryRoot,
                pantsAssemblyPath,
                midge,
                cancellationToken).ConfigureAwait(false);
        }

        _ = await RunEngineAsync(
            secondEngineIsMidge,
            $"{mode}-assert",
            databasePath,
            allProducerList,
            temporaryRoot,
            pantsAssemblyPath,
            midge,
            cancellationToken).ConfigureAwait(false);
        _ = await RunEngineAsync(
            firstEngineIsMidge,
            $"{mode}-assert",
            databasePath,
            allProducerList,
            temporaryRoot,
            pantsAssemblyPath,
            midge,
            cancellationToken).ConfigureAwait(false);

        await RunOfflineVerifiersAsync(
            mode,
            databasePath,
            temporaryRoot,
            pantsAssemblyPath,
            midge,
            cancellationToken).ConfigureAwait(false);
    }

    static async Task RunOfflineVerifiersAsync(
        string mode,
        string databasePath,
        string workingDirectory,
        string pantsAssemblyPath,
        MidgeDriverBuild midge,
        CancellationToken cancellationToken)
    {
        var beforeFingerprint = DirectoryTreeFingerprint.Compute(databasePath);
        _ = await RunEngineAsync(
            useMidge: false,
            $"{mode}-verify",
            databasePath,
            operand: null,
            workingDirectory,
            pantsAssemblyPath,
            midge,
            cancellationToken).ConfigureAwait(false);
        _ = await RunEngineAsync(
            useMidge: true,
            $"{mode}-verify",
            databasePath,
            operand: null,
            workingDirectory,
            pantsAssemblyPath,
            midge,
            cancellationToken).ConfigureAwait(false);
        var afterFingerprint = DirectoryTreeFingerprint.Compute(databasePath);
        if (!StringComparer.Ordinal.Equals(beforeFingerprint, afterFingerprint))
        {
            throw new InvalidOperationException(
                $"Offline verifier pair mutated '{databasePath}': before={beforeFingerprint}, "
                + $"after={afterFingerprint}.");
        }

        Console.WriteLine($"verifier-pair hash={afterFingerprint}");
    }

    static async Task<ProcessResult> RunEngineAsync(
        bool useMidge,
        string command,
        string databasePath,
        string? operand,
        string workingDirectory,
        string pantsAssemblyPath,
        MidgeDriverBuild midge,
        CancellationToken cancellationToken)
    {
        var executable = useMidge ? midge.ExecutablePath : "dotnet";
        var arguments = new List<string>();
        if (!useMidge)
        {
            arguments.Add(pantsAssemblyPath);
        }

        arguments.Add(command);
        arguments.Add(databasePath);
        if (operand is not null)
        {
            arguments.Add(operand);
        }

        var result = await ProcessRunner.RunAsync(
            executable,
            arguments,
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine(
            $"child engine={(useMidge ? "midge" : "pants")} "
            + $"elapsed={FormatSeconds(result.Elapsed)}s exit={result.ExitCode} "
            + $"command={result.DisplayCommand}");
        return result;
    }

    static void AssertSstFileCount(
        string databasePath,
        bool requireNone,
        string scenarioName,
        string boundary)
    {
        var count = Directory.Exists(databasePath)
            ? Directory.EnumerateFiles(databasePath, "*.sst", SearchOption.AllDirectories).Count()
            : 0;
        var valid = requireNone ? count == 0 : count >= 1;
        if (!valid)
        {
            var expectation = requireNone ? "zero" : "at least one";
            throw new InvalidOperationException(
                $"Scenario '{scenarioName}' expected {expectation} SST files after {boundary}, "
                + $"but found {count}.");
        }

        Console.WriteLine($"storage scenario={scenarioName} boundary={boundary} sst-files={count}");
    }

    static string FormatSeconds(TimeSpan elapsed) =>
        elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
}
