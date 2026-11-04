using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsL0FailedFreezeAdmissionTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task ShouldPreserveLastL0SlotAdmissionAndWalAcrossAutomaticFreezeFailure(
        bool crossFlushThreshold,
        bool failAutomaticFreeze,
        bool startManualCompaction)
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithMemtableLimits(4 * 1024 * 1024, 64 * 1024)
            // A source larger than the target requires physical work even when its range is
            // disjoint. The held output is essential to the last-slot admission schedule.
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: 2, TargetSstSizeBytes: 128, BackgroundEnabled: false));
        var plan = RuntimePlan.Resolve(options);
        var ceiling = MemtableWritePressure.GetL0HardCeiling(plan);
        var expected = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        await using (var seed = await PantsDatabase.OpenAsync(options))
        {
            for (var index = 0; index < ceiling - 1; index++)
            {
                var key = $"seed-{index:D2}";
                var value = TestBytes.FromString(key);
                await PutAsync(seed, key, value);
                await seed.Maintenance.FlushAsync(seed.ColumnFamilies.DefaultFamily);
                expected.Add(key, value);
            }

            Assert.Equal(ceiling - 1, await CountL0FilesAsync(seed));
            await seed.ShutdownAsync(TestTimeouts.Expected);
        }

        using var held = new HeldCompactionAndFreezeFailure();
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options.WithFlushAfterWalRecordsForTesting(1), new RuntimeDependencies(held)))
        {
            var compaction = startManualCompaction
                ? database.Maintenance.CompactAllAsync().AsTask()
                : Task.CompletedTask;
            try
            {
                if (startManualCompaction)
                {
                    await held.WaitForCompactionAsync();
                }
                Assert.Equal(ceiling - 1, await CountL0FilesAsync(database));
                if (failAutomaticFreeze)
                {
                    held.FailNextAutomaticFreeze();
                }

                var firstValue = new byte[checked((int)(crossFlushThreshold
                    ? plan.MemtableFlushThresholdBytes
                    : plan.MemtableFlushThresholdBytes / 4))];
                await PutAsync(database, "accepted", firstValue);
                expected.Add("accepted", firstValue);
                if (!startManualCompaction)
                {
                    await held.WaitForCompactionAsync();
                }
                if (!failAutomaticFreeze)
                {
                    await held.WaitForFlushAsync();
                }

                var before = await database.Diagnostics.GetRuntimeMetricsAsync();
                Assert.Equal(failAutomaticFreeze ? 1 : 0, held.AutomaticFreezeFailures);
                Assert.Equal(failAutomaticFreeze ? 0 : 1, before.ImmutableMemtables);
                if (failAutomaticFreeze)
                {
                    Assert.True(crossFlushThreshold
                        ? before.ActiveMemtableBytes >= plan.MemtableFlushThresholdBytes
                        : before.ActiveMemtableBytes < plan.MemtableFlushThresholdBytes);
                    Assert.True(before.ActiveMemtableBytes > 0);
                }
                else
                {
                    Assert.Equal(0, before.ActiveMemtableBytes);
                }

                var walBefore = ReadWalFiles(directory.Path);
                var secondValue = "next-value"u8.ToArray();
                if (crossFlushThreshold)
                {
                    await Assert.ThrowsAsync<PantsWriteStallException>(() =>
                        PutAsync(database, "next", secondValue));
                    var after = await database.Diagnostics.GetRuntimeMetricsAsync();
                    Assert.Equal(before.CurrentSequence, after.CurrentSequence);
                    Assert.Equal(before.ManifestNextWalSequence, after.ManifestNextWalSequence);
                    Assert.Equal(before.WalBytesWrittenTotal, after.WalBytesWrittenTotal);
                    Assert.Equal(before.WalPendingWrites, after.WalPendingWrites);
                    var walAfter = ReadWalFiles(directory.Path);
                    Assert.Equal(walBefore.Keys, walAfter.Keys);
                    foreach (var (name, bytes) in walBefore)
                    {
                        Assert.Equal(bytes, walAfter[name]);
                    }

                    Assert.False(await database.Maintenance.WaitForWriteStallClearAsync(
                        database.ColumnFamilies.DefaultFamily, TimeSpan.Zero));
                }
                else
                {
                    await PutAsync(database, "next", secondValue);
                    expected.Add("next", secondValue);
                    Assert.True((await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence > before.CurrentSequence);
                }

                await AssertExactRowsAsync(database, expected);
            }
            finally
            {
                held.Release();
                await compaction.WaitAsync(TestTimeouts.Expected);
            }

            await TestWait.UntilAsync(async () => await CountL0FilesAsync(database) < ceiling - 1,
                "pressure recovery publishes without ordinary background compaction");
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await AssertExactRowsAsync(database, expected);
            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertExactRowsAsync(reopened, expected);
    }

    static async Task PutAsync(IPantsDatabase database, string key, byte[] value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), value);
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async Task<int> CountL0FilesAsync(IPantsDatabase database) =>
        (await database.Diagnostics.GetStorageLayoutAsync()).Levels
        .FirstOrDefault(static level => level.Level == 0)?.FileCount ?? 0;

    static SortedDictionary<string, byte[]> ReadWalFiles(string path) => new(
        Directory.EnumerateFiles(Path.Combine(path, "wal"))
            .ToDictionary(static file => Path.GetFileName(file), File.ReadAllBytes, StringComparer.Ordinal),
        StringComparer.Ordinal);

    static async Task AssertExactRowsAsync(IPantsDatabase database, SortedDictionary<string, byte[]> expected)
    {
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var actual = new List<(string Key, byte[] Value)>();
        await foreach (var row in scan)
        {
            actual.Add((TestBytes.ToText(row.Key), row.Value.ToArray()));
        }

        Assert.Equal(expected.Keys, actual.Select(static row => row.Key));
        foreach (var (key, value) in actual)
        {
            Assert.Equal(expected[key], value);
            Assert.Equal(value, (await reader.GetAsync(TestBytes.FromString(key)))?.ToArray());
        }
    }

    sealed class HeldCompactionAndFreezeFailure : IFailpointHandler, IDisposable
    {
        readonly ManualResetEventSlim _release = new(false);
        readonly TaskCompletionSource _compaction = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _flush = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _failFreeze;
        int _freezeFailures;

        public int AutomaticFreezeFailures => Volatile.Read(ref _freezeFailures);

        public void FailNextAutomaticFreeze() => Volatile.Write(ref _failFreeze, 1);

        public Task WaitForCompactionAsync() => _compaction.Task.WaitAsync(TestTimeouts.Expected);

        public Task WaitForFlushAsync() => _flush.Task.WaitAsync(TestTimeouts.Expected);

        public void Release() => _release.Set();

        public void Hit(Failpoint failpoint)
        {
            if (failpoint == Failpoint.BeforeWalRecordThresholdFlush && Interlocked.Exchange(ref _failFreeze, 0) != 0)
            {
                Interlocked.Increment(ref _freezeFailures);
                throw new IOException("The accepted generation could not enter its automatic freeze.");
            }

            var entered = failpoint switch
            {
                Failpoint.AfterCompactionOutputDurable => _compaction,
                Failpoint.BeforeFlushPublication => _flush,
                _ => null
            };
            if (entered is null)
            {
                return;
            }

            entered.TrySetResult();
            if (!_release.Wait(TestTimeouts.Expected))
            {
                throw new TimeoutException($"Timed out waiting to release {failpoint}.");
            }
        }

        public void Dispose()
        {
            Release();
            _release.Dispose();
        }
    }
}
