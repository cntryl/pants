using System.Diagnostics;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

[Collection(CrashProcessTestGroup.Name)]
public sealed class MidgeFamilyCrashParityTests
{
    const string DatabasePathEnvironmentVariable = "PANTS_MIDGE_FAMILY_CRASH_DATABASE";
    const string ReadyPathEnvironmentVariable = "PANTS_MIDGE_FAMILY_CRASH_READY";

    static readonly string[] Keys = ["key:00", "key:01", "key:02", "key:03", "key:04", "key:05"];

    [Fact]
    public async Task ShouldPreserveCreatedFamiliesAfterProcessCrashAndPowerLoss()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "live");
        var powerLossImagePath = Path.Combine(directory.Path, "power-loss-image");
        using (var child = await StartReadyCrashChildAsync(databasePath))
        {
            child.TryKillProcessTree();
            await child.Process.WaitForExitAsync().WaitAsync(TestTimeouts.Expected);
            Assert.NotEqual(0, child.ExitCode);
        }

        await ExpireCrashedProcessLeaseAsync(databasePath);
        var expected = CreateExpectedState();
        expected["default"][Keys[0]] = "default-before-crash";
        expected["created-before-crash-a"][Keys[1]] = "family-a-before-crash";
        expected["created-before-crash-b"][Keys[2]] = "family-b-before-crash";
        await using (var recovered = await PantsDatabase.OpenAsync(CreateOptions(databasePath)))
        {
            await AssertRecoveredStateAsync(recovered, expected);

            var family = await recovered.ColumnFamilies.CreateAsync("created-after-crash");
            expected["created-after-crash"] = Keys.ToDictionary(
                static key => key,
                static _ => (string?)null,
                StringComparer.Ordinal);
            await PutAsync(recovered, family, Keys[3], "family-after-crash");
            expected["created-after-crash"][Keys[3]] = "family-after-crash";

            await AssertCurrentWalIsDurableAsync(recovered);
            CopyCrashImage(databasePath, powerLossImagePath);
            ExpireCrashedProcessLease(powerLossImagePath);
            await recovered.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var afterPowerLoss = await PantsDatabase.OpenAsync(CreateOptions(powerLossImagePath));
        await AssertRecoveredStateAsync(afterPowerLoss, expected);
    }

    [Fact]
    public async Task ShouldAbortAfterCreatingFamiliesBeforePowerLossInChild()
    {
        var databasePath = Environment.GetEnvironmentVariable(DatabasePathEnvironmentVariable);
        var readyPath = Environment.GetEnvironmentVariable(ReadyPathEnvironmentVariable);
        if (databasePath is null || readyPath is null)
        {
            return;
        }

        await using var database = await PantsDatabase.OpenAsync(CreateOptions(databasePath));
        await PutAsync(
            database,
            database.ColumnFamilies.DefaultFamily,
            Keys[0],
            "default-before-crash");

        var familyA = await database.ColumnFamilies.CreateAsync("created-before-crash-a");
        await PutAsync(database, familyA, Keys[1], "family-a-before-crash");
        var familyB = await database.ColumnFamilies.CreateAsync("created-before-crash-b");
        await PutAsync(database, familyB, Keys[2], "family-b-before-crash");
        await File.WriteAllTextAsync(readyPath, "ready");

        using var process = Process.GetCurrentProcess();
        try
        {
            process.Kill();
        }
        finally
        {
            Environment.Exit(137);
        }
    }

    static Dictionary<string, Dictionary<string, string?>> CreateExpectedState() =>
        new(StringComparer.Ordinal)
        {
            ["default"] = EmptyValues(),
            ["created-before-crash-a"] = EmptyValues(),
            ["created-before-crash-b"] = EmptyValues()
        };

    static Dictionary<string, string?> EmptyValues() =>
        Keys.ToDictionary(static key => key, static _ => (string?)null, StringComparer.Ordinal);

    static async Task AssertCurrentWalIsDurableAsync(IPantsDatabase database)
    {
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(PantsEngineHealth.Healthy, metrics.Health);
        Assert.True(metrics.CurrentSequence > 0);
        Assert.Equal(metrics.CurrentSequence, metrics.WalLastSyncedSequence);
        Assert.Equal(metrics.CurrentSequence, metrics.WalLocalDurableSequence);
        Assert.Equal(0, metrics.WalPendingWrites);
    }

    static async Task AssertRecoveredStateAsync(
        IPantsDatabase database,
        Dictionary<string, Dictionary<string, string?>> expected)
    {
        var families = await database.ColumnFamilies.ListAsync();
        Assert.Equal(
            expected.Keys.Order(StringComparer.Ordinal),
            families.Select(static family => family.Name).Order(StringComparer.Ordinal));

        foreach (var familyState in expected)
        {
            var family = Assert.IsAssignableFrom<IPantsColumnFamily>(
                await database.ColumnFamilies.GetAsync(familyState.Key));
            await using var reader = await database.Transactions.BeginAsync(
                family,
                PantsTransactionMode.ReadOnly);
            foreach (var key in Keys)
            {
                var value = await reader.GetAsync(TestBytes.FromString(key));
                Assert.Equal(
                    familyState.Value[key],
                    value is { } bytes ? TestBytes.ToText(bytes) : null);
            }

            await using var scan = await reader.ScanAsync(new PantsScanQuery());
            var scanned = new List<(string Key, string Value)>();
            await foreach (var entry in scan)
            {
                scanned.Add((TestBytes.ToText(entry.Key), TestBytes.ToText(entry.Value)));
            }

            var expectedRows = familyState.Value
                .Where(static pair => pair.Value is not null)
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => (pair.Key, pair.Value!))
                .ToArray();
            Assert.Equal(expectedRows, scanned);
        }
    }

    static async Task PutAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key,
        string value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.Local(path).WithBackgroundCompaction(false);

    static Task<CrashChildProcess> StartReadyCrashChildAsync(string databasePath)
    {
        var readyPath = Path.Combine(databasePath, "midge-family-crash.ready");
        var start = CrashChildProcess.CreateStartInfo(
            typeof(MidgeFamilyCrashParityTests),
            nameof(ShouldAbortAfterCreatingFamiliesBeforePowerLossInChild));
        start.Environment[DatabasePathEnvironmentVariable] = databasePath;
        start.Environment[ReadyPathEnvironmentVariable] = readyPath;
        return CrashChildProcess.StartAndWaitForReadinessAsync(
            start,
            "Midge family crash child",
            databasePath,
            readyPath);
    }

    static async Task ExpireCrashedProcessLeaseAsync(string path)
    {
        var leasePath = Path.Combine(path, ".midge_leader");
        var lines = await File.ReadAllLinesAsync(leasePath);
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith("acquired_at: ", StringComparison.Ordinal))
            {
                lines[index] = "acquired_at: 1970-01-01T00:00:00Z";
            }
        }

        await File.WriteAllLinesAsync(
            leasePath,
            lines.Where(static line => !line.StartsWith("checksum: ", StringComparison.Ordinal)));
        File.Delete(Path.Combine(path, ".midge_leader.lock"));
    }

    static void ExpireCrashedProcessLease(string path)
    {
        var leasePath = Path.Combine(path, ".midge_leader");
        var lines = File.ReadAllLines(leasePath)
            .Where(static line => !line.StartsWith("checksum: ", StringComparison.Ordinal))
            .Select(static line => line.StartsWith("acquired_at: ", StringComparison.Ordinal)
                ? "acquired_at: 1970-01-01T00:00:00Z"
                : line)
            .ToArray();
        File.WriteAllLines(leasePath, lines);
    }

    static void CopyCrashImage(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var sourceDirectory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, sourceDirectory)));
        }

        foreach (var sourceFile in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source, sourceFile);
            if (relativePath is "LOCK" or ".midge_leader.lock")
            {
                continue;
            }

            var destinationFile = Path.Combine(destination, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, true);
        }
    }
}
