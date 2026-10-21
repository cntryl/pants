using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeSimulatedCloudControlParityTests
{
    static readonly string[] Keys =
    [
        "key-0",
        "key-1",
        "key-2",
        "key-3",
        "key-4",
        "key-5"
    ];

    [Fact]
    public async Task ShouldPreserveSimulatedCloudWritesAcrossCleanRestartAndPowerLoss()
    {
        using var directory = new TemporaryDirectory();
        var livePath = Path.Combine(directory.Path, "live");
        var crashImagePath = Path.Combine(directory.Path, "crash-image");
        var options = CreateOptions(livePath);
        var expected = CreateExpectedState();

        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = database.ColumnFamilies.DefaultFamily;
            await PutAsync(database, family, Keys[0], "strict-before-clean-restart",
                PantsWriteOptions.CloudStrict);
            expected[Keys[0]] = "strict-before-clean-restart";

            await PutAsync(database, family, Keys[1], "async-before-strict-barrier",
                PantsWriteOptions.CloudAsync);
            expected[Keys[1]] = "async-before-strict-barrier";
            await PutAsync(database, family, Keys[2], "strict-barrier",
                PantsWriteOptions.CloudStrict);
            expected[Keys[2]] = "strict-barrier";

            await PutAsync(database, family, Keys[3], "deleted-value",
                PantsWriteOptions.CloudStrict);
            expected[Keys[3]] = "deleted-value";
            await DeleteAsync(database, family, Keys[3], PantsWriteOptions.CloudStrict);
            expected[Keys[3]] = null;

            await PutAsync(database, family, Keys[4], "async-before-flush",
                PantsWriteOptions.CloudAsync);
            expected[Keys[4]] = "async-before-flush";
            await database.Maintenance.FlushAsync(family);
            await database.Maintenance.CompactAllAsync();
            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        await using (var reopened = await PantsDatabase.OpenAsync(options))
        {
            await AssertRecoveredStateAsync(reopened, expected);

            var family = reopened.ColumnFamilies.DefaultFamily;
            await PutAsync(reopened, family, Keys[5], "async-after-clean-restart",
                PantsWriteOptions.CloudAsync);
            expected[Keys[5]] = "async-after-clean-restart";
            await PutAsync(reopened, family, Keys[0], "strict-after-clean-restart",
                PantsWriteOptions.CloudStrict);
            expected[Keys[0]] = "strict-after-clean-restart";
            await PutAsync(reopened, family, Keys[2], "async-before-final-flush",
                PantsWriteOptions.CloudAsync);
            expected[Keys[2]] = "async-before-final-flush";
            await PutAsync(reopened, family, Keys[0], "strict-after-final-async",
                PantsWriteOptions.CloudStrict);
            expected[Keys[0]] = "strict-after-final-async";
            await FlushAndCompactAsync(reopened, family);

            var metrics = await reopened.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(PantsEngineHealth.Healthy, metrics.Health);
            Assert.True(metrics.WalCloudDurableSequence >= metrics.CurrentSequence);
            Assert.Equal(0, metrics.PendingCloudUploads);

            CopyCrashImage(livePath, crashImagePath);
            ExpireWriterLease(crashImagePath);
            await reopened.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var recovered = await PantsDatabase.OpenAsync(CreateOptions(crashImagePath));
        await AssertRecoveredStateAsync(recovered, expected);
    }

    static Dictionary<string, string?> CreateExpectedState() =>
        Keys.ToDictionary(static key => key, static _ => (string?)null, StringComparer.Ordinal);

    static async Task AssertRecoveredStateAsync(
        IPantsDatabase database,
        Dictionary<string, string?> expected)
    {
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        foreach (var key in Keys)
        {
            var value = await reader.GetAsync(TestBytes.FromString(key));
            Assert.Equal(
                expected[key],
                value is { } bytes ? TestBytes.ToText(bytes) : null);
        }

        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var scanned = new List<(string Key, string Value)>();
        await foreach (var entry in scan)
        {
            scanned.Add((TestBytes.ToText(entry.Key), TestBytes.ToText(entry.Value)));
        }

        var expectedRows = expected
            .Where(static pair => pair.Value is not null)
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => (pair.Key, pair.Value!))
            .ToArray();
        Assert.Equal(expectedRows, scanned);
        Assert.Equal(
            scanned.Select(static row => row.Key).Distinct(StringComparer.Ordinal).Count(),
            scanned.Count);
    }

    static async Task PutAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key,
        string value,
        PantsWriteOptions options)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(options);
    }

    static async Task DeleteAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key,
        PantsWriteOptions options)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Delete(TestBytes.FromString(key));
        await transaction.CommitAsync(options);
    }

    static async Task FlushAndCompactAsync(IPantsDatabase database, IPantsColumnFamily family)
    {
        await database.Maintenance.FlushAsync(family);
        await database.Maintenance.CompactAllAsync();
    }

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.SimulatedCloud(path, "pants-tests", "midge-simulated-cloud-control/")
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                128,
                long.MaxValue,
                TimeSpan.FromHours(1),
                2))
            .WithBackgroundCompaction(false);

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

    static void ExpireWriterLease(string path)
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
}
