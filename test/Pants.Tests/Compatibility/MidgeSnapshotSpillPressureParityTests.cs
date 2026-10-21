using Cntryl.Pants.Scan;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeSnapshotSpillPressureParityTests
{
    const int RowCount = 32;
    const int UncommittedCount = 128;
    const int ValueLength = 8 * 1024;
    const int SpillValueLength = 32 * 1024;
    const long LocalStorageBudgetBytes = 2L * 1024 * 1024;

    [Fact]
    public async Task ShouldPreserveSnapshotIsolationWhenSpillPressureIsReleased()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch.AddMilliseconds(10_000));
        var options = PantsOpenOptions
            .SimulatedCloud(directory.Path, "pants-tests", "snapshot-spill-pressure/")
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(16L * 1024 * 1024))
            .WithSimulatedCloudLocalStorageBudget(LocalStorageBudgetBytes)
            .WithMemtableLimits(256 * 1024)
            .WithTransactionMemoryPool(128 * 1024)
            .WithTtlClock(clock)
            .WithBackgroundCompaction(false)
            .WithStorageTimeout(TimeSpan.FromSeconds(2))
            .WithRuntimeResponseTimeout(TimeSpan.FromSeconds(10));

        SortedDictionary<string, byte[]> expected;
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = await database.ColumnFamilies.CreateAsync("data");
            var initial = await SeedRowsAsync(database, family);
            var cloudSstDirectory = Path.Combine(directory.Path, "cloud_store", "sst");
            var originalSstNames = await GetActiveSstNamesAsync(database);
            Assert.NotEmpty(originalSstNames);
            Assert.All(originalSstNames, name =>
                Assert.True(File.Exists(Path.Combine(cloudSstDirectory, name)), name));

            var old = await database.Transactions.BeginAsync(
                family,
                PantsTransactionMode.ReadOnly);
            var oldDisposed = false;
            try
            {
                await AssertExactAsync(old, initial);

                clock.UtcNow = clock.UtcNow.AddSeconds(2);
                var expiredExpected = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var row in initial.Where(static pair => StringComparer.Ordinal.Compare(
                             pair.Key,
                             "row-008") >= 0))
                {
                    expiredExpected.Add(row.Key, row.Value);
                }

                await using (var expired = await database.Transactions.BeginAsync(
                                 family,
                                 PantsTransactionMode.ReadOnly))
                {
                    await AssertExactAsync(expired, expiredExpected);
                }

                expected = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
                for (var round = 0; round < 16; round++)
                {
                    var candidate = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
                    await using (var rewrite = await database.Transactions.BeginAsync(
                                     family,
                                     PantsTransactionMode.ReadWrite))
                    {
                        rewrite.DeleteRange(KeyBytes("row-008"), KeyBytes("row-024"));
                        for (var index = 0; index < RowCount; index++)
                        {
                            if (index is >= 8 and < 24)
                            {
                                continue;
                            }

                            var key = RowKey(index);
                            var value = Payload(checked((uint)(1000 + round * RowCount + index)), ValueLength);
                            rewrite.Put(KeyBytes(key), value);
                            candidate.Add(key, value);
                        }

                        await rewrite.CommitAsync(PantsWriteOptions.CloudStrict);
                    }

                    expected = candidate;
                    await database.Maintenance.FlushAsync(family);
                    await database.Maintenance.CompactAllAsync();
                    await AssertExactAsync(old, initial);
                    await using var current = await database.Transactions.BeginAsync(
                        family,
                        PantsTransactionMode.ReadOnly);
                    await AssertExactAsync(current, expected);
                }

                await using (var hog = await ExhaustSpillCapacityAsync(database, family, directory.Path))
                {
                    await AssertRejectsWriteUnderPressureAsync(database, family);
                    await AssertExactAsync(old, initial);
                    await using var current = await database.Transactions.BeginAsync(
                        family,
                        PantsTransactionMode.ReadOnly);
                    await AssertExactAsync(current, expected);
                }

                await using (var resumed = await database.Transactions.BeginAsync(
                                 family,
                                 PantsTransactionMode.ReadWrite))
                {
                    var releasedValue = new byte[64 * 1024];
                    Array.Fill(releasedValue, (byte)7);
                    resumed.Put(KeyBytes("released"), releasedValue);
                    await resumed.CommitAsync(PantsWriteOptions.CloudStrict);
                    expected.Add("released", releasedValue);
                }

                await AssertExactAsync(old, initial);
                await using (var current = await database.Transactions.BeginAsync(
                                 family,
                                 PantsTransactionMode.ReadOnly))
                {
                    await AssertExactAsync(current, expected);
                }

                var retiredSstPaths = (await GetActiveSstNamesAsync(database))
                    .ToHashSet(StringComparer.Ordinal);
                var retiredCloudSsts = originalSstNames
                    .Where(name => !retiredSstPaths.Contains(name))
                    .Select(name => Path.Combine(cloudSstDirectory, name))
                    .ToArray();
                Assert.NotEmpty(retiredCloudSsts);
                Assert.All(retiredCloudSsts, path => Assert.True(File.Exists(path), path));

                await old.DisposeAsync();
                oldDisposed = true;
                await WaitForCloudSstReclamationAsync(retiredCloudSsts);

                await database.Maintenance.CompactAllAsync();
                await using (var progress = await database.Transactions.BeginAsync(
                                 family,
                                 PantsTransactionMode.ReadWrite))
                {
                    progress.Put(KeyBytes("released"), KeyBytes("progress"));
                    await progress.CommitAsync(PantsWriteOptions.CloudStrict);
                }

                await database.Maintenance.FlushAsync(family);
                expected["released"] = KeyBytes("progress");
            }
            finally
            {
                if (!oldDisposed)
                {
                    await old.DisposeAsync();
                }
            }
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var reopenedFamily = await reopened.ColumnFamilies.GetAsync("data")
            ?? throw new InvalidOperationException("The persisted data column family was not reopened.");
        await using var reader = await reopened.Transactions.BeginAsync(
            reopenedFamily,
            PantsTransactionMode.ReadOnly);
        await AssertExactAsync(reader, expected);
    }

    static async Task<SortedDictionary<string, byte[]>> SeedRowsAsync(
        IPantsDatabase database,
        IPantsColumnFamily family)
    {
        var initial = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        for (var index = 0; index < RowCount; index++)
        {
            var key = RowKey(index);
            var value = Payload(checked((uint)(index + 1)), ValueLength);
            await using var transaction = await database.Transactions.BeginAsync(
                family,
                PantsTransactionMode.ReadWrite);
            transaction.Put(
                KeyBytes(key),
                value,
                index < 8 ? TimeSpan.FromSeconds(1) : null);
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
            initial.Add(key, value);
        }

        await database.Maintenance.FlushAsync(family);
        return initial;
    }

    static async Task<IPantsTransaction> ExhaustSpillCapacityAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string root)
    {
        var beforeHog = Assert.IsType<PantsHybridStorageBudgetSnapshot>(
            (await database.Diagnostics.GetRuntimeMetricsAsync()).HybridStorageBudget);
        var availableBytes = beforeHog.MaximumLocalBytes - beforeHog.TotalCommittedBytes;
        Assert.True(availableBytes > 0, "The setup must leave local capacity for caller-owned spills.");
        var minimumSpillBytes = checked(availableBytes * 3 / 4);
        var hog = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite);
        try
        {
            var spillBoundaryReached = false;
            for (var index = 0; index < UncommittedCount; index++)
            {
                try
                {
                    var value = new byte[SpillValueLength];
                    Array.Fill(value, (byte)9);
                    hog.Put(KeyBytes($"uncommitted-{index:000}"), value);
                }
                catch (PantsNoSpaceException)
                {
                    spillBoundaryReached = true;
                    break;
                }
            }

            Assert.True(spillBoundaryReached, "The transaction must exhaust actual spill capacity.");
            var transactionDirectory = Path.Combine(root, "txn");
            var spillBytes = Directory
                .EnumerateFiles(transactionDirectory, "*", SearchOption.AllDirectories)
                .Sum(static path => new FileInfo(path).Length);
            var storage = Assert.IsType<PantsHybridStorageBudgetSnapshot>(
                (await database.Diagnostics.GetRuntimeMetricsAsync()).HybridStorageBudget);
            Assert.True(
                spillBytes >= minimumSpillBytes,
                $"Caller-owned spill files must consume at least 75% of the {availableBytes} bytes free before the hog; " +
                $"found {spillBytes} bytes " +
                $"(ledger: {storage.TotalCommittedBytes} total, {storage.Usage.WalBytes} WAL, " +
                $"{storage.Usage.TransactionSpillBytes} spill, {storage.Usage.ResidentSstBytes} SST, " +
                $"{storage.Usage.StartupResidueBytes} startup residue, " +
                $"{storage.Usage.FlushStagingReservedBytes} flush staging, " +
                $"{storage.Usage.FlushHeadroomReservedBytes} flush headroom).");
            Assert.Equal(spillBytes, storage.Usage.TransactionSpillBytes);
            Assert.True(
                storage.TotalCommittedBytes >= storage.MaximumLocalBytes * 9 / 10,
                $"The pressure boundary must leave the local budget at least 90% occupied: " +
                $"{storage.TotalCommittedBytes}/{storage.MaximumLocalBytes} bytes.");
            return hog;
        }
        catch
        {
            await hog.DisposeAsync();
            throw;
        }
    }

    static async Task AssertRejectsWriteUnderPressureAsync(
        IPantsDatabase database,
        IPantsColumnFamily family)
    {
        await using var blocked = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        PantsException? pressureError = null;
        try
        {
            var value = new byte[64 * 1024];
            Array.Fill(value, (byte)7);
            blocked.Put(KeyBytes("released"), value);
            await blocked.CommitAsync(PantsWriteOptions.CloudStrict);
        }
        catch (PantsException exception)
        {
            pressureError = exception;
        }

        Assert.NotNull(pressureError);
        Assert.True(
            pressureError.Code is PantsErrorCode.NoSpace or PantsErrorCode.WriteStall or PantsErrorCode.Busy,
            $"Unexpected admission failure: {pressureError.Code}.");
    }

    static async Task AssertExactAsync(
        IPantsTransaction transaction,
        SortedDictionary<string, byte[]> expected)
    {
        var rows = new List<(string Key, byte[] Value)>();
        await using (var scan = await transaction.ScanAsync(new PantsScanQuery()))
        {
            await foreach (var entry in scan)
            {
                rows.Add((TestBytes.ToText(entry.Key), entry.Value.ToArray()));
            }
        }

        var actualKeys = rows.Select(static row => row.Key).ToArray();
        Assert.Equal(actualKeys.Order(StringComparer.Ordinal).ToArray(), actualKeys);
        Assert.Equal(actualKeys.Length, actualKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expected.Keys.ToArray(), actualKeys);
        foreach (var (key, value) in rows)
        {
            Assert.Equal(expected[key], value);
        }

        for (var index = 0; index < RowCount; index++)
        {
            var key = RowKey(index);
            var result = await transaction.GetAsync(KeyBytes(key));
            if (expected.TryGetValue(key, out var value))
            {
                Assert.Equal(value, Assert.IsType<ReadOnlyMemory<byte>>(result).ToArray());
            }
            else
            {
                Assert.Null(result);
            }
        }

        for (var index = 0; index < UncommittedCount; index++)
        {
            Assert.Null(await transaction.GetAsync(KeyBytes($"uncommitted-{index:000}")));
        }

        var released = await transaction.GetAsync(KeyBytes("released"));
        if (expected.TryGetValue("released", out var releasedValue))
        {
            Assert.Equal(releasedValue, Assert.IsType<ReadOnlyMemory<byte>>(released).ToArray());
        }
        else
        {
            Assert.Null(released);
        }
    }

    static async Task<string[]> GetActiveSstNamesAsync(IPantsDatabase database) =>
        (await database.Diagnostics.GetStorageLayoutAsync())
        .Levels
        .SelectMany(static level => level.Files)
        .Select(static file => file.Name)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    static async Task WaitForCloudSstReclamationAsync(IReadOnlyList<string> paths)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (paths.Any(File.Exists))
        {
            Assert.True(
                DateTime.UtcNow < deadline,
                "Cloud SST inputs pinned by the old snapshot must be reclaimed after it closes.");
            await Task.Delay(10);
        }
    }

    static byte[] Payload(uint seed, int length)
    {
        var value = new byte[length];
        for (var index = 0; index < value.Length; index++)
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            value[index] = (byte)seed;
        }

        return value;
    }

    static string RowKey(int index) => $"row-{index:000}";

    static byte[] KeyBytes(string value) => TestBytes.FromString(value);
}
