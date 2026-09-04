using System.Diagnostics;
using System.Runtime.InteropServices;
using Cntryl.Pants.Support.TestDoubles;
using Xunit.Sdk;

namespace Cntryl.Pants.Storage;

[Collection(CrashProcessTestGroup.Name)]
public sealed class PantsDiskResidentDifferentialTests
{
    const string CrashDatabasePathEnvironmentVariable = "PANTS_DIFFERENTIAL_CRASH_DATABASE_PATH";
    const string CrashStorageEnvironmentVariable = "PANTS_DIFFERENTIAL_CRASH_STORAGE";
    const string CrashReadyFileName = "differential-crash.ready";
    const int KeyCount = 16;

    [Fact]
    public async Task ShouldAbortAfterWritingDifferentialWalChildScenario()
    {
        var storage = Environment.GetEnvironmentVariable(CrashStorageEnvironmentVariable);
        if (storage is not ("local" or "simulated-cloud"))
        {
            return;
        }

        var path = Assert.IsType<string>(
            Environment.GetEnvironmentVariable(CrashDatabasePathEnvironmentVariable));
        var simulatedCloud = StringComparer.Ordinal.Equals(storage, "simulated-cloud");
        var clock = new ManualClock(DateTimeOffset.UnixEpoch.AddHours(1));
        await using var database = await PantsDatabase.OpenAsync(
            CreateOptions(path, simulatedCloud, clock));
        await ApplyCrashMutationsAsync(database, simulatedCloud);
        await File.WriteAllTextAsync(Path.Combine(path, CrashReadyFileName), "ready");

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldMatchTheModelAfterAbruptWalRecovery(bool simulatedCloud)
    {
        using var directory = new TemporaryDirectory();
        using var child = StartCrashChild(directory.Path, simulatedCloud);
        try
        {
            await WaitForCrashChildAsync(child, directory.Path);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(true);
            }

            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.NotEqual(0, child.ExitCode);
        if (simulatedCloud)
        {
            RemoveLocalCache(directory.Path);
        }
        else
        {
            await ExpireCrashedLeaseAsync(directory.Path);
        }

        var clock = new ManualClock(DateTimeOffset.UnixEpoch.AddHours(1));
        await using var reopened = await PantsDatabase.OpenAsync(
            CreateOptions(directory.Path, simulatedCloud, clock));
        await AssertMatchesAsync(reopened, CrashModel(clock.UtcNow));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldMatchTheModelAcrossFlushCompactionSnapshotTtlAndReopen(
        bool simulatedCloud)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch.AddHours(1));
        var options = CreateOptions(directory.Path, simulatedCloud, clock);
        var model = new SortedDictionary<string, ModelValue>(StringComparer.Ordinal);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var random = new Random(219);
            for (var step = 0; step < 48; step++)
            {
                if (step != 0 && step % 7 == 0)
                {
                    clock.UtcNow += TimeSpan.FromSeconds(2);
                    RemoveExpired(model, clock.UtcNow);
                }

                await ApplyRandomMutationAsync(
                    database,
                    model,
                    random,
                    step,
                    simulatedCloud,
                    clock.UtcNow);
                if ((step + 1) % 8 == 0)
                {
                    await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
                    await AssertMatchesAsync(database, model);
                }

                if ((step + 1) % 24 == 0)
                {
                    await database.Maintenance.CompactAllAsync();
                    await AssertMatchesAsync(database, model);
                }
            }

            RemoveExpired(model, clock.UtcNow);
            await using (var snapshot = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadOnly))
            {
                var snapshotModel = model.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value,
                    StringComparer.Ordinal);
                await PutAsync(database, "key:00", "after-snapshot", simulatedCloud);
                model["key:00"] = new ModelValue("after-snapshot", null);

                await AssertMatchesAsync(snapshot, snapshotModel);
            }

            await AssertMatchesAsync(database, model);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await database.Maintenance.CompactAllAsync();
        }

        if (simulatedCloud)
        {
            foreach (var path in Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"))
            {
                File.Delete(path);
            }
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertMatchesAsync(reopened, model);
        var metrics = await reopened.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(0, metrics.TotalMemtableBytes);
        Assert.Equal(0, metrics.ScanBufferUsedBytes);
        Assert.True(metrics.ScanBufferPeakBytes <= metrics.ScanBufferCapacityBytes);
    }

    static async Task ApplyRandomMutationAsync(
        IPantsDatabase database,
        IDictionary<string, ModelValue> model,
        Random random,
        int step,
        bool simulatedCloud,
        DateTimeOffset now)
    {
        RemoveExpired(model, now);
        var keyIndex = random.Next(KeyCount);
        var key = Key(keyIndex);
        var value = $"value:{step:D3}:{random.Next():X8}";
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        switch (random.Next(6))
        {
            case 0:
            case 1:
                transaction.Put(Bytes(key), Bytes(value));
                model[key] = new ModelValue(value, null);
                break;
            case 2:
                transaction.Delete(Bytes(key));
                model.Remove(key);
                break;
            case 3:
                var endIndex = Math.Min(KeyCount, keyIndex + random.Next(1, 5));
                transaction.DeleteRange(Bytes(key), Bytes(Key(endIndex)));
                foreach (var removed in model.Keys
                             .Where(candidate =>
                                 StringComparer.Ordinal.Compare(candidate, key) >= 0 &&
                                 StringComparer.Ordinal.Compare(candidate, Key(endIndex)) < 0)
                             .ToArray())
                {
                    model.Remove(removed);
                }

                break;
            case 4:
                var ttl = TimeSpan.FromSeconds(random.Next(1, 6));
                transaction.Put(Bytes(key), Bytes(value), ttl);
                model[key] = new ModelValue(value, now + ttl);
                break;
            default:
                if (model.ContainsKey(key))
                {
                    transaction.Put(Bytes(key), Bytes(value));
                }
                else
                {
                    transaction.Insert(Bytes(key), Bytes(value));
                }

                model[key] = new ModelValue(value, null);
                break;
        }

        await transaction.CommitAsync(
            simulatedCloud ? PantsWriteOptions.CloudStrict : PantsWriteOptions.Sync);
    }

    static async Task PutAsync(
        IPantsDatabase database,
        string key,
        string value,
        bool simulatedCloud)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(Bytes(key), Bytes(value));
        await transaction.CommitAsync(
            simulatedCloud ? PantsWriteOptions.CloudStrict : PantsWriteOptions.Sync);
    }

    static async Task ApplyCrashMutationsAsync(
        IPantsDatabase database,
        bool simulatedCloud)
    {
        var durability = simulatedCloud ? PantsWriteOptions.CloudStrict : PantsWriteOptions.Sync;
        await using (var first = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            first.Insert(Bytes(Key(0)), Bytes("alpha"));
            first.Put(Bytes(Key(1)), Bytes("beta"), TimeSpan.FromMinutes(10));
            first.Put(Bytes(Key(2)), Bytes("gamma"));
            await first.CommitAsync(durability);
        }

        await using (var second = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            second.Delete(Bytes(Key(2)));
            second.DeleteRange(Bytes(Key(4)), Bytes(Key(7)));
            await second.CommitAsync(durability);
        }

        await using (var third = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            third.Put(Bytes(Key(4)), Bytes("delta"));
            third.Put(Bytes(Key(7)), Bytes("epsilon"));
            await third.CommitAsync(durability);
        }

        await using var fourth = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        fourth.DeleteRange(Bytes(Key(0)), Bytes(Key(1)));
        fourth.Insert(Bytes(Key(2)), Bytes("zeta"));
        await fourth.CommitAsync(durability);
    }

    static SortedDictionary<string, ModelValue> CrashModel(DateTimeOffset now) =>
        new(StringComparer.Ordinal)
        {
            [Key(1)] = new ModelValue("beta", now + TimeSpan.FromMinutes(10)),
            [Key(2)] = new ModelValue("zeta", null),
            [Key(4)] = new ModelValue("delta", null),
            [Key(7)] = new ModelValue("epsilon", null)
        };

    static Process StartCrashChild(string path, bool simulatedCloud)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
                       Environment.ProcessPath ??
                       "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(PantsDiskResidentDifferentialTests).Assembly.Location);
        start.ArgumentList.Add($"/Platform:{RuntimeInformation.ProcessArchitecture}");
        start.ArgumentList.Add(
            $"--Tests:{typeof(PantsDiskResidentDifferentialTests).FullName}." +
            nameof(ShouldAbortAfterWritingDifferentialWalChildScenario));
        start.Environment[CrashDatabasePathEnvironmentVariable] = path;
        start.Environment[CrashStorageEnvironmentVariable] = simulatedCloud
            ? "simulated-cloud"
            : "local";
        return Process.Start(start) ?? throw new XunitException(
            "Could not start the differential crash child.");
    }

    static async Task WaitForCrashChildAsync(Process child, string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var readyPath = Path.Combine(path, CrashReadyFileName);
        while (!File.Exists(readyPath))
        {
            if (child.HasExited)
            {
                throw new XunitException(
                    $"Differential crash child exited with {child.ExitCode} before readiness.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    static async Task ExpireCrashedLeaseAsync(string path)
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

        await File.WriteAllLinesAsync(leasePath, lines);
        File.Delete(Path.Combine(path, ".midge_leader.lock"));
    }

    static void RemoveLocalCache(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            if (StringComparer.Ordinal.Equals(Path.GetFileName(path), "cloud_store"))
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    static async Task AssertMatchesAsync(
        IPantsDatabase database,
        IReadOnlyDictionary<string, ModelValue> model)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await AssertMatchesAsync(transaction, model);
    }

    static async Task AssertMatchesAsync(
        IPantsTransaction transaction,
        IReadOnlyDictionary<string, ModelValue> model)
    {
        for (var index = 0; index < KeyCount; index++)
        {
            var key = Key(index);
            var actual = await transaction.GetAsync(Bytes(key));
            Assert.Equal(
                model.TryGetValue(key, out var expected) ? expected.Value : null,
                actual is null ? null : TestBytes.ToText(actual.Value));
        }

        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        var actualRows = new List<KeyValuePair<string, string>>();
        await foreach (var entry in scan)
        {
            actualRows.Add(new KeyValuePair<string, string>(
                TestBytes.ToText(entry.Key),
                TestBytes.ToText(entry.Value)));
        }

        Assert.Equal(
            model.Select(static pair => new KeyValuePair<string, string>(
                pair.Key,
                pair.Value.Value)),
            actualRows);
    }

    static PantsOpenOptions CreateOptions(
        string path,
        bool simulatedCloud,
        IPantsClock clock) =>
        (simulatedCloud
            ? PantsOpenOptions.SimulatedCloud(path, "pants-tests", "differential/")
            : PantsOpenOptions.Local(path))
        .WithBackgroundCompaction(false)
        .WithTtlClock(clock);

    static void RemoveExpired(
        IDictionary<string, ModelValue> model,
        DateTimeOffset now)
    {
        foreach (var key in model
                     .Where(pair => pair.Value.ExpiresAt is { } expiry && now >= expiry)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            model.Remove(key);
        }
    }

    static string Key(int index) => $"key:{index:D2}";

    static byte[] Bytes(string value) => TestBytes.FromString(value);

    sealed record ModelValue(string Value, DateTimeOffset? ExpiresAt);
}
