using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeSyncedCrashControlParityTests
{
    static readonly string[] Keys =
    [
        "key:00",
        "key:01",
        "key:02",
        "key:03",
        "key:04",
        "key:05"
    ];

    [Fact]
    public async Task ShouldPreserveSyncedWriteHistoryAcrossSuccessivePowerLosses()
    {
        using var directory = new TemporaryDirectory();
        var livePath = Path.Combine(directory.Path, "live");
        var firstImagePath = Path.Combine(directory.Path, "first-crash-image");
        var secondImagePath = Path.Combine(directory.Path, "second-crash-image");
        var expected = CreateExpectedState();
        var options = CreateOptions(livePath);

        await using (var firstRun = await PantsDatabase.OpenAsync(options))
        {
            await PutAsync(firstRun, Keys[0], "first-run-a");
            expected[Keys[0]] = "first-run-a";
            await PutAsync(firstRun, Keys[1], "first-run-b");
            expected[Keys[1]] = "first-run-b";
            await PutAsync(firstRun, Keys[2], "first-run-c");
            expected[Keys[2]] = "first-run-c";
            await DeleteAsync(firstRun, Keys[1]);
            expected[Keys[1]] = null;
            await PutAsync(firstRun, Keys[1], "first-run-b-replaced");
            expected[Keys[1]] = "first-run-b-replaced";

            await AssertCurrentWalIsDurableAsync(firstRun);
            CopyCrashImage(livePath, firstImagePath);
            ExpireWriterLease(firstImagePath);
            await firstRun.ShutdownAsync(TestTimeouts.Expected);
        }

        await using (var firstRecovery = await PantsDatabase.OpenAsync(CreateOptions(firstImagePath)))
        {
            await AssertRecoveredStateAsync(firstRecovery, expected);

            await PutAsync(firstRecovery, Keys[2], "second-run-c");
            expected[Keys[2]] = "second-run-c";
            await DeleteAsync(firstRecovery, Keys[0]);
            expected[Keys[0]] = null;
            await PutAsync(firstRecovery, Keys[4], "second-run-e");
            expected[Keys[4]] = "second-run-e";

            await AssertCurrentWalIsDurableAsync(firstRecovery);
            CopyCrashImage(firstImagePath, secondImagePath);
            ExpireWriterLease(secondImagePath);
            await firstRecovery.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var secondRecovery = await PantsDatabase.OpenAsync(CreateOptions(secondImagePath));
        await AssertRecoveredStateAsync(secondRecovery, expected);
    }

    static Dictionary<string, string?> CreateExpectedState() =>
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
    }

    static async Task PutAsync(IPantsDatabase database, string key, string value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async Task DeleteAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Delete(TestBytes.FromString(key));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.Local(path).WithBackgroundCompaction(false);

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
